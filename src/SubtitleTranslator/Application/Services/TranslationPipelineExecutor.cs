using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Application.Interfaces;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Domain.ValueObjects;
using SubtitleTranslator.Infrastructure.Persistence;
using SubtitleTranslator.Infrastructure.Providers.AzureBatch;
using SubtitleTranslator.Srt;
using SubtitleTranslator.Srt.Chunking;
using SubtitleTranslator.Srt.Validation;

namespace SubtitleTranslator.Application.Services;

public sealed class TranslationPipelineExecutor(
    SubtitleTranslatorDbContext db,
    IPipelineDefinitionService pipelineDefinitionService,
    ITranslationProviderFactory providerFactory,
    IEnumerable<IBatchTranslationProvider> batchProviders,
    AzureBatchJobStateService batchStateService,
    SrtFileService srtFiles,
    SrtChunker chunker,
    SrtValidator validator,
    ILogger<TranslationPipelineExecutor> logger)
{
    private readonly IReadOnlyDictionary<string, IBatchTranslationProvider> batchProvidersByName =
        batchProviders.ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);

    public async Task<TranslationExecutionResult> ExecuteJobAsync(
        int jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken)
            ?? throw new KeyNotFoundException($"Translation job {jobId} was not found.");
        if (job.Status == JobStatus.Completed)
        {
            return new(job.Id, true, job.OutputPath, job.CompletedChunks, 0, 0, job.EstimatedCost, []);
        }

        if (job.PipelineId is null || job.PromptVersionId is null)
        {
            throw new InvalidOperationException("Translation job must reference a pipeline and a prompt version.");
        }

        var promptVersion = await db.PromptVersions
            .Include(version => version.PromptTemplate)
            .SingleOrDefaultAsync(version => version.Id == job.PromptVersionId.Value, cancellationToken)
            ?? throw new InvalidOperationException($"Prompt version {job.PromptVersionId} was not found.");
        if (!promptVersion.Enabled)
        {
            throw new InvalidOperationException("The prompt version assigned to this job is disabled.");
        }

        var steps = await pipelineDefinitionService.GetEnabledStepsAsync(job.PipelineId.Value, cancellationToken);
        if (steps.Count == 0)
        {
            throw new InvalidOperationException($"Pipeline {job.PipelineId} has no enabled translation steps.");
        }

        var source = await srtFiles.ReadAsync(job.SourcePath, cancellationToken);
        var translated = await LoadCompletedTranslationsAsync(job, source, cancellationToken);
        var firstModel = steps[0].ModelDefinition!;
        var logicalChunks = CreateChunks(source, firstModel);
        var errors = new List<string>();
        var lastSequence = await db.Chunks.Where(chunk => chunk.JobId == job.Id)
            .Select(chunk => (int?)chunk.Sequence).MaxAsync(cancellationToken) ?? 0;
        var sequence = new SequenceCounter(lastSequence);

        job.Status = JobStatus.Preparing;
        job.StartedAt ??= DateTimeOffset.UtcNow;
        job.TotalChunks = logicalChunks.Count;
        job.CompletedChunks = logicalChunks.Count(chunk => chunk.Subtitles.All(subtitle => translated.ContainsKey(subtitle.Index)));
        job.FailedChunks = 0;
        job.LastError = null;
        Touch(job);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            foreach (var step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var model = step.ModelDefinition!;
                var provider = model.Provider!;
                var pending = source.Where(subtitle => !translated.ContainsKey(subtitle.Index)).ToArray();
                if (pending.Length == 0)
                {
                    break;
                }

                IReadOnlyList<SrtChunk> stepChunks;
                try
                {
                    stepChunks = CreateChunks(pending, model);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    var message = $"Pipeline step {step.Order} ({provider.Name}/{model.Name}) is invalid: {exception.Message}";
                    errors.Add(message);
                    logger.LogWarning(exception, "Skipping invalid translation pipeline step {StepId} for job {JobId}", step.Id, job.Id);
                    continue;
                }

                job.CurrentProvider = provider.Name;
                job.CurrentModel = model.Name;
                job.Status = model.SupportsBatch && batchProvidersByName.ContainsKey(provider.Type.ToString())
                    ? JobStatus.AzureBatchSubmitting
                    : provider.Type switch
                    {
                        ProviderType.OpenRouter => JobStatus.OpenRouterProcessing,
                        ProviderType.AzureOpenAi => JobStatus.AzureOpenAiProcessing,
                        ProviderType.Ollama => JobStatus.LocalProcessing,
                        _ => JobStatus.Retrying
                    };
                Touch(job);
                await db.SaveChangesAsync(cancellationToken);

                if (model.SupportsBatch && batchProvidersByName.TryGetValue(provider.Type.ToString(), out var batchProvider))
                {
                    await ProcessBatchStepAsync(
                        job, provider, model, promptVersion, batchProvider, stepChunks,
                        translated, logicalChunks, errors, sequence, cancellationToken);
                }
                else
                {
                    await ProcessSynchronousStepAsync(
                        job, provider, model, promptVersion, stepChunks,
                        translated, logicalChunks, errors, sequence, cancellationToken);
                }
            }

            var failedIndexes = source.Where(subtitle => !translated.ContainsKey(subtitle.Index))
                .Select(subtitle => subtitle.Index).ToHashSet();
            job.CompletedChunks = logicalChunks.Count(chunk => chunk.Subtitles.All(subtitle => translated.ContainsKey(subtitle.Index)));
            job.FailedChunks = logicalChunks.Count(chunk => chunk.Subtitles.Any(subtitle => failedIndexes.Contains(subtitle.Index)));
            await FinalizeRetryPendingChunksAsync(job.Id, cancellationToken);

            if (failedIndexes.Count > 0)
            {
                job.Status = JobStatus.Failed;
                job.LastError = errors.LastOrDefault() ?? $"Translation failed for {failedIndexes.Count} subtitle(s).";
                Touch(job);
                await db.SaveChangesAsync(cancellationToken);
                var attempts = await CountAttemptsAsync(job.Id, cancellationToken);
                return new(job.Id, false, null, translated.Count, failedIndexes.Count, attempts, job.EstimatedCost, errors);
            }

            job.Status = JobStatus.Validating;
            var output = source.Select(subtitle => translated[subtitle.Index]).ToArray();
            var validation = validator.Validate(source, output);
            if (!validation.IsValid)
            {
                job.Status = JobStatus.Failed;
                job.LastError = string.Join(" ", validation.Errors);
                job.FailedChunks = logicalChunks.Count;
                Touch(job);
                await db.SaveChangesAsync(cancellationToken);
                errors.AddRange(validation.Errors);
                var attempts = await CountAttemptsAsync(job.Id, cancellationToken);
                return new(job.Id, false, null, translated.Count, source.Count, attempts, job.EstimatedCost, errors);
            }

            var outputPath = string.IsNullOrWhiteSpace(job.OutputPath)
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(job.SourcePath))!, $"{Path.GetFileNameWithoutExtension(job.SourcePath)}.pt-BR.srt")
                : job.OutputPath;
            await WriteOutputAtomicallyAsync(outputPath, output, cancellationToken);
            job.OutputPath = outputPath;
            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.CompletedChunks = logicalChunks.Count;
            job.FailedChunks = 0;
            Touch(job);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Translation job {JobId} completed with {SubtitleCount} subtitles at {OutputPath}", job.Id, source.Count, outputPath);
            var totalAttempts = await CountAttemptsAsync(job.Id, cancellationToken);
            return new(job.Id, true, outputPath, source.Count, 0, totalAttempts, job.EstimatedCost, errors);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.Status = JobStatus.Cancelled;
            job.LastError = "Translation was cancelled.";
            var inFlightChunks = await db.Chunks
                .Where(chunk => chunk.JobId == job.Id && (chunk.Status == ChunkStatus.Processing || chunk.Status == ChunkStatus.RetryPending))
                .ToListAsync(CancellationToken.None);
            foreach (var chunk in inFlightChunks)
            {
                chunk.Status = ChunkStatus.Cancelled;
                chunk.UpdatedAt = DateTimeOffset.UtcNow;
            }
            Touch(job);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            job.Status = JobStatus.Failed;
            job.LastError = exception.Message;
            job.FailedChunks = logicalChunks.Count(chunk => chunk.Subtitles.Any(subtitle => !translated.ContainsKey(subtitle.Index)));
            await FinalizeRetryPendingChunksAsync(job.Id, CancellationToken.None);
            Touch(job);
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogError(exception, "Translation job {JobId} failed unexpectedly", job.Id);
            errors.Add(exception.Message);
            var attempts = await CountAttemptsAsync(job.Id, CancellationToken.None);
            return new(job.Id, false, null, translated.Count, source.Count - translated.Count, attempts, job.EstimatedCost, errors);
        }
    }

    private async Task ProcessSynchronousStepAsync(
        TranslationJob job,
        Provider provider,
        ModelDefinition model,
        PromptVersion promptVersion,
        IReadOnlyList<SrtChunk> chunks,
        IDictionary<int, Subtitle> translated,
        IReadOnlyList<SrtChunk> logicalChunks,
        ICollection<string> errors,
        SequenceCounter sequence,
        CancellationToken cancellationToken)
    {
        ITranslationProvider adapter;
        try
        {
            adapter = providerFactory.GetRequired(provider.ToConfiguration());
        }
        catch (Exception exception)
        {
            foreach (var srtChunk in chunks)
            {
                var chunk = CreateAttemptChunk(job, provider, model, srtChunk.Subtitles, ++sequence.Value);
                db.Chunks.Add(chunk);
                await db.SaveChangesAsync(cancellationToken);
                RecordFailedAttempt(job, chunk, provider, model, exception);
                chunk.Status = ChunkStatus.RetryPending;
                chunk.Error = exception.Message;
                errors.Add($"{provider.Name}/{model.Name}: {exception.Message}");
            }

            Touch(job);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        foreach (var srtChunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pendingInChunk = srtChunk.Subtitles.Where(subtitle => !translated.ContainsKey(subtitle.Index)).ToArray();
            if (pendingInChunk.Length == 0)
            {
                continue;
            }

            var chunk = CreateAttemptChunk(job, provider, model, pendingInChunk, ++sequence.Value);
            db.Chunks.Add(chunk);
            await db.SaveChangesAsync(cancellationToken);
            var maxAttempts = Math.Clamp(ReadInt(model.ConfigurationJson, "pipelineMaxAttempts") ?? 1, 1, 5);
            var retryDelayMilliseconds = Math.Clamp(ReadInt(model.ConfigurationJson, "pipelineRetryDelayMilliseconds") ?? 1000, 0, 60_000);
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                chunk.Attempt = attempt;
                try
                {
                    var request = CreateTranslationRequest(provider, model, promptVersion, pendingInChunk);
                    var result = await adapter.TranslateAsync(request, cancellationToken);
                    var validation = validator.Validate(pendingInChunk, result.Subtitles);
                    if (!validation.IsValid)
                    {
                        throw new InvalidDataException(string.Join(" ", validation.Errors));
                    }

                    foreach (var subtitle in result.Subtitles)
                    {
                        translated[subtitle.Index] = subtitle;
                    }

                    RecordSuccessfulAttempt(job, chunk, provider, model, result.Usage);
                    chunk.ResultJson = SerializeTranslatedSubtitles(result.Subtitles);
                    chunk.Status = ChunkStatus.Completed;
                    chunk.CompletedAt = DateTimeOffset.UtcNow;
                    chunk.Error = null;
                    UpdateProgress(job, logicalChunks, translated);
                    Touch(job);
                    await db.SaveChangesAsync(cancellationToken);
                    break;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    RecordFailedAttempt(job, chunk, provider, model, exception);
                    chunk.Status = attempt < maxAttempts ? ChunkStatus.Processing : ChunkStatus.RetryPending;
                    chunk.Error = exception.Message;
                    errors.Add($"{provider.Name}/{model.Name}: {exception.Message}");
                    UpdateProgress(job, logicalChunks, translated);
                    Touch(job);
                    await db.SaveChangesAsync(cancellationToken);
                    logger.LogWarning(exception, "Translation chunk {ChunkId} attempt {Attempt}/{MaxAttempts} failed for job {JobId} using {Provider}/{Model}", chunk.Id, attempt, maxAttempts, job.Id, provider.Name, model.Name);
                    if (attempt < maxAttempts && retryDelayMilliseconds > 0)
                    {
                        await Task.Delay(retryDelayMilliseconds, cancellationToken);
                    }
                }
            }
        }
    }

    private async Task ProcessBatchStepAsync(
        TranslationJob job,
        Provider provider,
        ModelDefinition model,
        PromptVersion promptVersion,
        IBatchTranslationProvider batchProvider,
        IReadOnlyList<SrtChunk> chunks,
        IDictionary<int, Subtitle> translated,
        IReadOnlyList<SrtChunk> logicalChunks,
        ICollection<string> errors,
        SequenceCounter sequence,
        CancellationToken cancellationToken)
    {
        var items = new List<BatchTranslationItem>();
        var chunksByCustomId = new Dictionary<string, TranslationChunk>(StringComparer.Ordinal);
        foreach (var srtChunk in chunks)
        {
            var pendingInChunk = srtChunk.Subtitles.Where(subtitle => !translated.ContainsKey(subtitle.Index)).ToArray();
            if (pendingInChunk.Length == 0)
            {
                continue;
            }

            var chunk = CreateAttemptChunk(job, provider, model, pendingInChunk, ++sequence.Value);
            db.Chunks.Add(chunk);
            await db.SaveChangesAsync(cancellationToken);
            var customId = $"job-{job.Id}-chunk-{chunk.Id}";
            chunksByCustomId.Add(customId, chunk);
            items.Add(new BatchTranslationItem(customId, pendingInChunk, "en", "pt-BR", promptVersion.GlossaryJson));
        }

        if (items.Count == 0)
        {
            return;
        }

        string? activeBatchId = null;
        try
        {
            var submitted = await batchProvider.SubmitBatchAsync(
                provider.ToConfiguration(), model.ToConfiguration(), promptVersion.StaticContent, items, cancellationToken);
            activeBatchId = submitted.BatchId;
            await batchStateService.RecordSubmissionAsync(job.Id, submitted, cancellationToken);
            var status = await PollBatchAsync(job, provider, model, batchProvider, submitted, cancellationToken);
            await batchStateService.RecordStatusAsync(job.Id, status, cancellationToken);
            var batchResult = await batchProvider.GetResultsAsync(provider.ToConfiguration(), status.BatchId, cancellationToken);
            BatchErrorResult batchErrors;
            try
            {
                batchErrors = await batchProvider.GetErrorsAsync(provider.ToConfiguration(), status.BatchId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not retrieve Azure Batch error file for job {JobId}, batch {BatchId}", job.Id, status.BatchId);
                errors.Add($"Could not retrieve Azure Batch error file: {exception.Message}");
                batchErrors = new BatchErrorResult([]);
            }
            var errorsByCustomId = batchErrors.Errors
                .Where(error => !string.IsNullOrWhiteSpace(error.CustomId))
                .GroupBy(error => error.CustomId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var resultsById = batchResult.Lines
                .Where(line => !string.IsNullOrWhiteSpace(line.CustomId))
                .GroupBy(line => line.CustomId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (var (customId, chunk) in chunksByCustomId)
            {
                if (!resultsById.TryGetValue(customId, out var line))
                {
                    var message = errorsByCustomId.TryGetValue(customId, out var batchError)
                        ? batchError.Message
                        : $"Azure Batch produced no result for '{customId}'.";
                    FailBatchChunk(job, provider, model, chunk, new InvalidDataException(message), errors);
                    chunk.Attempts.Last().AzureBatchId = status.BatchId;
                    continue;
                }

                if (line.Error is not null || line.StatusCode is < 200 or >= 300 || line.ResponseBody is null)
                {
                    var message = line.Error?.Message ?? $"Azure Batch request returned HTTP {line.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.";
                    FailBatchChunk(job, provider, model, chunk, new AzureBatchProviderException(message, line.StatusCode), errors);
                    chunk.Attempts.Last().AzureBatchId = status.BatchId;
                    continue;
                }

                try
                {
                    var sourceSubtitles = chunk.JobId == job.Id
                        ? items.Single(item => item.CustomId == customId).Subtitles
                        : [];
                    var body = line.ResponseBody.Value;
                    var content = body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        throw new InvalidDataException("Azure Batch returned an empty completion.");
                    }

                    var translatedSubtitles = ParseSubtitleJson(content, sourceSubtitles);
                    var validation = validator.Validate(sourceSubtitles, translatedSubtitles);
                    if (!validation.IsValid)
                    {
                        throw new InvalidDataException(string.Join(" ", validation.Errors));
                    }

                    foreach (var subtitle in translatedSubtitles)
                    {
                        translated[subtitle.Index] = subtitle;
                    }

                    var usageElement = body.TryGetProperty("usage", out var usageJson) ? usageJson : default;
                    var usage = new TranslationUsage(
                        ReadLong(usageElement, "prompt_tokens"),
                        ReadLong(usageElement, "completion_tokens"),
                        ReadLong(GetProperty(usageElement, "prompt_tokens_details"), "cached_tokens"));
                    RecordSuccessfulAttempt(job, chunk, provider, model, usage);
                    chunk.Attempts.Last().AzureBatchId = status.BatchId;
                    chunk.ResultJson = SerializeTranslatedSubtitles(translatedSubtitles);
                    chunk.Status = ChunkStatus.Completed;
                    chunk.CompletedAt = DateTimeOffset.UtcNow;
                    chunk.Error = null;
                    UpdateProgress(job, logicalChunks, translated);
                    Touch(job);
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    FailBatchChunk(job, provider, model, chunk, exception, errors);
                    chunk.Attempts.Last().AzureBatchId = status.BatchId;
                    UpdateProgress(job, logicalChunks, translated);
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!string.IsNullOrWhiteSpace(job.AzureBatchId) && !IsTerminalBatchStatus(job.AzureBatchStatus ?? string.Empty))
            {
                try
                {
                    await batchProvider.CancelBatchAsync(provider.ToConfiguration(), job.AzureBatchId, CancellationToken.None);
                }
                catch (Exception cancellationException)
                {
                    logger.LogWarning(cancellationException, "Could not cancel Azure Batch {BatchId} for job {JobId}", job.AzureBatchId, job.Id);
                }
            }

            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            foreach (var chunk in chunksByCustomId.Values.Where(chunk => chunk.Status == ChunkStatus.Processing))
            {
                FailBatchChunk(job, provider, model, chunk, exception, errors);
                chunk.Attempts.Last().AzureBatchId = activeBatchId;
            }

            Touch(job);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<BatchStatusResult> PollBatchAsync(
        TranslationJob job,
        Provider provider,
        ModelDefinition model,
        IBatchTranslationProvider batchProvider,
        BatchSubmissionResult submission,
        CancellationToken cancellationToken)
    {
        var status = new BatchStatusResult(
            submission.BatchId,
            submission.Status,
            submission.InputFileId,
            null,
            null,
            submission.CreatedAt,
            null,
            submission.ExpiresAt,
            null,
            DateTimeOffset.UtcNow,
            submission.TotalRequests,
            submission.CompletedRequests,
            submission.FailedRequests,
            []);
        var pollSeconds = ReadInt(model.ConfigurationJson, "batchPollIntervalSeconds") ?? 60;
        if (IsTerminalBatchStatus(status.Status))
        {
            status = await batchProvider.GetStatusAsync(provider.ToConfiguration(), submission.BatchId, cancellationToken);
            await batchStateService.RecordStatusAsync(job.Id, status, cancellationToken);
            return status;
        }

        while (!IsTerminalBatchStatus(status.Status))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pollSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), cancellationToken);
            }

            status = await batchProvider.GetStatusAsync(provider.ToConfiguration(), submission.BatchId, cancellationToken);
            await batchStateService.RecordStatusAsync(job.Id, status, cancellationToken);
        }

        return status;
    }

    private static bool IsTerminalBatchStatus(string status) =>
        status.Equals("completed", StringComparison.OrdinalIgnoreCase)
        || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
        || status.Equals("expired", StringComparison.OrdinalIgnoreCase)
        || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
        || status.Equals("canceled", StringComparison.OrdinalIgnoreCase);

    private TranslationRequest CreateTranslationRequest(
        Provider provider,
        ModelDefinition model,
        PromptVersion promptVersion,
        IReadOnlyList<Subtitle> subtitles) => new()
        {
            Provider = provider.ToConfiguration(),
            Model = model.ToConfiguration(),
            Subtitles = subtitles,
            Prompt = promptVersion.StaticContent,
            GlossaryJson = promptVersion.GlossaryJson
        };

    private static TranslationChunk CreateAttemptChunk(
        TranslationJob job,
        Provider provider,
        ModelDefinition model,
        IReadOnlyList<Subtitle> subtitles,
        int sequence) => new()
        {
            Job = job,
            Sequence = sequence,
            StartSubtitleIndex = subtitles.Min(subtitle => subtitle.Index),
            EndSubtitleIndex = subtitles.Max(subtitle => subtitle.Index),
            Status = ChunkStatus.Processing,
            Provider = provider.Name,
            Model = model.Name,
            Attempt = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static void RecordSuccessfulAttempt(
        TranslationJob job,
        TranslationChunk chunk,
        Provider provider,
        ModelDefinition model,
        TranslationUsage usage)
    {
        var cost = TranslationCostCalculator.Calculate(usage, model);
        chunk.InputTokens += usage.InputTokens;
        chunk.OutputTokens += usage.OutputTokens;
        chunk.LatencyMs = (chunk.LatencyMs ?? 0) + usage.LatencyMs;
        chunk.UpdatedAt = DateTimeOffset.UtcNow;
        chunk.Attempts.Add(new TranslationAttempt
        {
            AttemptNumber = chunk.Attempt,
            Provider = provider.Name,
            Model = model.Name,
            Success = true,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CachedTokens = usage.CachedTokens,
            CacheWriteTokens = usage.CacheWriteTokens,
            LatencyMs = usage.LatencyMs,
            EstimatedCost = cost
        });
        job.InputTokens += usage.InputTokens;
        job.OutputTokens += usage.OutputTokens;
        job.EstimatedCost += cost;
    }

    private static void RecordFailedAttempt(
        TranslationJob job,
        TranslationChunk chunk,
        Provider provider,
        ModelDefinition model,
        Exception exception)
    {
        chunk.Attempt++;
        chunk.UpdatedAt = DateTimeOffset.UtcNow;
        chunk.Attempts.Add(new TranslationAttempt
        {
            AttemptNumber = chunk.Attempt,
            Provider = provider.Name,
            Model = model.Name,
            Success = false,
            ErrorCode = exception.GetType().Name,
            ErrorMessage = exception.Message
        });
        job.RetryCount++;
    }

    private static void FailBatchChunk(
        TranslationJob job,
        Provider provider,
        ModelDefinition model,
        TranslationChunk chunk,
        Exception exception,
        ICollection<string> errors)
    {
        RecordFailedAttempt(job, chunk, provider, model, exception);
        chunk.Status = ChunkStatus.RetryPending;
        chunk.Error = exception.Message;
        errors.Add($"{provider.Name}/{model.Name}: {exception.Message}");
    }

    private static void UpdateProgress(
        TranslationJob job,
        IReadOnlyList<SrtChunk> logicalChunks,
        IDictionary<int, Subtitle> translated)
    {
        job.CompletedChunks = logicalChunks.Count(chunk => chunk.Subtitles.All(subtitle => translated.ContainsKey(subtitle.Index)));
        job.UpdatedAt = DateTimeOffset.UtcNow;
        job.LastActivityAt = job.UpdatedAt;
    }

    private async Task FinalizeRetryPendingChunksAsync(int jobId, CancellationToken cancellationToken)
    {
        var chunks = await db.Chunks.Where(chunk => chunk.JobId == jobId && chunk.Status == ChunkStatus.RetryPending)
            .ToListAsync(cancellationToken);
        foreach (var chunk in chunks)
        {
            chunk.Status = ChunkStatus.Failed;
            chunk.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task<Dictionary<int, Subtitle>> LoadCompletedTranslationsAsync(
        TranslationJob job,
        IReadOnlyList<Subtitle> source,
        CancellationToken cancellationToken)
    {
        var translated = new Dictionary<int, Subtitle>();
        var completedChunks = await db.Chunks
            .Where(chunk => chunk.JobId == job.Id && chunk.Status == ChunkStatus.Completed && chunk.ResultJson != null)
            .OrderBy(chunk => chunk.Sequence)
            .ToListAsync(cancellationToken);
        foreach (var chunk in completedChunks)
        {
            try
            {
                foreach (var subtitle in ParseSubtitleJson(chunk.ResultJson!, source))
                {
                    translated.TryAdd(subtitle.Index, subtitle);
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or KeyNotFoundException)
            {
                logger.LogWarning(exception, "Ignoring invalid persisted translation result for chunk {ChunkId}", chunk.Id);
                chunk.Status = ChunkStatus.RetryPending;
                chunk.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        if (completedChunks.Any(chunk => chunk.Status == ChunkStatus.RetryPending))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return translated;
    }

    private static string SerializeTranslatedSubtitles(IEnumerable<Subtitle> subtitles) =>
        JsonSerializer.Serialize(subtitles.Select(subtitle => new { index = subtitle.Index, text = subtitle.Text }));

    private Task<int> CountAttemptsAsync(int jobId, CancellationToken cancellationToken) =>
        db.Attempts.CountAsync(attempt => attempt.Chunk != null && attempt.Chunk.JobId == jobId, cancellationToken);

    private async Task WriteOutputAtomicallyAsync(
        string outputPath,
        IReadOnlyList<Subtitle> subtitles,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await srtFiles.WriteAsync(temporaryPath, subtitles, cancellationToken);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private IReadOnlyList<SrtChunk> CreateChunks(IReadOnlyList<Subtitle> subtitles, ModelDefinition model)
    {
        var limit = model.ChunkStrategy switch
        {
            ChunkStrategy.MaxTokens => model.MaxChunkTokens,
            ChunkStrategy.MaxSubtitleCount => model.MaxChunkSubtitles,
            _ => null
        };
        if (limit is null or <= 0)
        {
            throw new InvalidOperationException($"Model '{model.Name}' must have a positive limit for chunk strategy '{model.ChunkStrategy}'.");
        }

        return chunker.Chunk(subtitles, model.ChunkStrategy, limit.Value);
    }

    private static IReadOnlyList<Subtitle> ParseSubtitleJson(string content, IReadOnlyList<Subtitle> source)
    {
        var normalized = content.Trim();
        if (normalized.StartsWith("```", StringComparison.Ordinal))
        {
            normalized = normalized.Trim('`', ' ', '\r', '\n');
            if (normalized.StartsWith("json\n", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[5..];
            }
        }

        using var document = JsonDocument.Parse(normalized);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Translation output must be a JSON array.");
        }

        var sourceByIndex = source.ToDictionary(subtitle => subtitle.Index);
        var translated = new List<Subtitle>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var index = item.GetProperty("index").GetInt32();
            var text = item.GetProperty("text").GetString();
            if (!sourceByIndex.TryGetValue(index, out var original))
            {
                throw new InvalidDataException($"Translation output contains unknown subtitle index {index}.");
            }

            translated.Add(new Subtitle
            {
                Index = original.Index,
                Start = original.Start,
                End = original.End,
                Text = text ?? string.Empty
            });
        }

        return translated;
    }

    private static int? ReadInt(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
                ? Math.Max(0, number)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement GetProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static long ReadLong(JsonElement element, string name)
    {
        var value = GetProperty(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    }

    private static void Touch(TranslationJob job)
    {
        job.UpdatedAt = DateTimeOffset.UtcNow;
        job.LastActivityAt = job.UpdatedAt;
    }

    private sealed class SequenceCounter(int value)
    {
        public int Value { get; set; } = value;
    }
}
