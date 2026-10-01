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
using SubtitleTranslator.Srt.Validation;

namespace SubtitleTranslator.Application.Services;

public sealed class TranslationRecoveryService(
    SubtitleTranslatorDbContext db,
    IEnumerable<IBatchTranslationProvider> batchProviders,
    AzureBatchJobStateService batchStateService,
    SrtFileService srtFiles,
    SrtValidator validator,
    ILogger<TranslationRecoveryService> logger)
{
    private readonly IReadOnlyDictionary<string, IBatchTranslationProvider> batchProvidersByName =
        batchProviders.ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);

    public async Task RecoverAsync(
        TimeSpan stalledThreshold,
        TimeSpan batchPollInterval,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await RecoverDueBatchesAsync(now, stalledThreshold, batchPollInterval, cancellationToken);
        await RecoverStalledJobsAsync(now, stalledThreshold, cancellationToken);
    }

    private async Task RecoverDueBatchesAsync(
        DateTimeOffset now,
        TimeSpan stalledThreshold,
        TimeSpan batchPollInterval,
        CancellationToken cancellationToken)
    {
        var batchCandidates = await db.Jobs
            .Where(job => job.AzureBatchId != null)
            .ToListAsync(cancellationToken);
        var dueBefore = now - batchPollInterval;
        var jobs = batchCandidates
            .Where(job => !IsTerminal(job.AzureBatchStatus ?? string.Empty)
                && (job.AzureBatchLastPolledAt is null || job.AzureBatchLastPolledAt <= dueBefore))
            .ToArray();

        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = await FindCurrentStepAsync(job, cancellationToken);
            if (step?.ModelDefinition?.Provider is not { } provider
                || !batchProvidersByName.TryGetValue(provider.Type.ToString(), out var batchProvider))
            {
                logger.LogWarning("Cannot resume Azure Batch {BatchId} for job {JobId}: provider configuration is unavailable", job.AzureBatchId, job.Id);
                continue;
            }

            try
            {
                var status = await batchProvider.GetStatusAsync(provider.ToConfiguration(), job.AzureBatchId!, cancellationToken);
                var previousProgressAt = job.AzureBatchLastProgressAt;
                await batchStateService.RecordStatusAsync(job.Id, status, cancellationToken);
                if (previousProgressAt is not null && now - previousProgressAt > stalledThreshold
                    && !IsTerminal(status.Status))
                {
                    logger.LogWarning(
                        "Azure Batch {BatchId} for job {JobId} has made no measurable progress for {StalledFor}",
                        status.BatchId,
                        job.Id,
                        now - previousProgressAt.Value);
                }

                if (IsTerminal(status.Status))
                {
                    await ApplyBatchFilesAsync(job, provider, step.ModelDefinition!, batchProvider, status, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not recover Azure Batch {BatchId} for job {JobId}", job.AzureBatchId, job.Id);
            }
        }
    }

    private async Task ApplyBatchFilesAsync(
        TranslationJob job,
        Provider provider,
        ModelDefinition model,
        IBatchTranslationProvider batchProvider,
        BatchStatusResult status,
        CancellationToken cancellationToken)
    {
        var inFlight = await db.Chunks
            .Include(chunk => chunk.Attempts)
            .Where(chunk => chunk.JobId == job.Id && chunk.Status == ChunkStatus.Processing)
            .ToListAsync(cancellationToken);
        if (inFlight.Count == 0)
        {
            job.Status = JobStatus.Retrying;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var source = await srtFiles.ReadAsync(job.SourcePath, cancellationToken);
        var output = await batchProvider.GetResultsAsync(provider.ToConfiguration(), status.BatchId, cancellationToken);
        BatchErrorResult errors;
        try
        {
            errors = await batchProvider.GetErrorsAsync(provider.ToConfiguration(), status.BatchId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not retrieve error output for resumed batch {BatchId}", status.BatchId);
            errors = new BatchErrorResult([]);
        }

        var outputById = output.Lines
            .Where(line => !string.IsNullOrWhiteSpace(line.CustomId))
            .GroupBy(line => line.CustomId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var errorsById = errors.Errors
            .Where(error => !string.IsNullOrWhiteSpace(error.CustomId))
            .GroupBy(error => error.CustomId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var chunk in inFlight)
        {
            var customId = $"job-{job.Id}-chunk-{chunk.Id}";
            if (!outputById.TryGetValue(customId, out var line)
                || line.ResponseBody is null
                || line.Error is not null
                || line.StatusCode is < 200 or >= 300)
            {
                var message = line?.Error?.Message
                    ?? (errorsById.TryGetValue(customId, out var error) ? error.Message : "Azure Batch result is missing or failed.");
                RecordFailure(job, chunk, provider, model, status.BatchId, message);
                continue;
            }

            try
            {
                var subtitles = source.Where(subtitle => subtitle.Index >= chunk.StartSubtitleIndex && subtitle.Index <= chunk.EndSubtitleIndex).ToArray();
                var body = line.ResponseBody.Value;
                var content = body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new InvalidDataException("Azure Batch returned an empty completion.");
                }

                var translated = ParseSubtitleJson(content, subtitles);
                var validation = validator.Validate(subtitles, translated);
                if (!validation.IsValid)
                {
                    throw new InvalidDataException(string.Join(" ", validation.Errors));
                }

                chunk.ResultJson = JsonSerializer.Serialize(translated.Select(subtitle => new { index = subtitle.Index, text = subtitle.Text }));
                chunk.Status = ChunkStatus.Completed;
                chunk.CompletedAt = DateTimeOffset.UtcNow;
                chunk.UpdatedAt = chunk.CompletedAt.Value;
                chunk.Error = null;
                var usage = ReadUsage(body);
                chunk.InputTokens += usage.InputTokens;
                chunk.OutputTokens += usage.OutputTokens;
                var cost = TranslationCostCalculator.Calculate(usage, model);
                job.InputTokens += usage.InputTokens;
                job.OutputTokens += usage.OutputTokens;
                job.EstimatedCost += cost;
                chunk.Attempts.Add(new TranslationAttempt
                {
                    AttemptNumber = Math.Max(1, chunk.Attempt),
                    Provider = provider.Name,
                    Model = model.Name,
                    AzureBatchId = status.BatchId,
                    Success = true,
                    InputTokens = usage.InputTokens,
                    OutputTokens = usage.OutputTokens,
                    CachedTokens = usage.CachedTokens,
                    CacheWriteTokens = usage.CacheWriteTokens,
                    LatencyMs = usage.LatencyMs,
                    EstimatedCost = cost
                });
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or KeyNotFoundException)
            {
                RecordFailure(job, chunk, provider, model, status.BatchId, exception.Message);
            }
        }

        job.Status = JobStatus.Retrying;
        job.LastHeartbeatAt = DateTimeOffset.UtcNow;
        job.LastActivityAt = job.LastHeartbeatAt;
        job.UpdatedAt = job.LastHeartbeatAt.Value;
        await db.SaveChangesAsync(cancellationToken);
    }

    private void RecordFailure(
        TranslationJob job,
        TranslationChunk chunk,
        Provider provider,
        ModelDefinition model,
        string batchId,
        string message)
    {
        chunk.Status = ChunkStatus.RetryPending;
        chunk.Error = message;
        chunk.UpdatedAt = DateTimeOffset.UtcNow;
        chunk.Attempts.Add(new TranslationAttempt
        {
            AttemptNumber = Math.Max(1, chunk.Attempt),
            Provider = provider.Name,
            Model = model.Name,
            AzureBatchId = batchId,
            Success = false,
            ErrorCode = "AzureBatchRequestFailed",
            ErrorMessage = message
        });
        job.RetryCount++;
    }

    private async Task RecoverStalledJobsAsync(
        DateTimeOffset now,
        TimeSpan stalledThreshold,
        CancellationToken cancellationToken)
    {
        var recoverableStatuses = new[]
        {
            JobStatus.Preparing,
            JobStatus.OpenRouterProcessing,
            JobStatus.AzureOpenAiProcessing,
            JobStatus.LocalProcessing,
            JobStatus.Validating
        };
        var recoverableJobs = await db.Jobs
            .Where(job => recoverableStatuses.Contains(job.Status))
            .ToListAsync(cancellationToken);
        var staleBefore = now - stalledThreshold;
        var stalledJobs = recoverableJobs
            .Where(job => (job.LastHeartbeatAt ?? job.LastActivityAt ?? job.UpdatedAt) <= staleBefore)
            .ToArray();

        foreach (var job in stalledJobs)
        {
            var processingChunks = await db.Chunks
                .Where(chunk => chunk.JobId == job.Id && chunk.Status == ChunkStatus.Processing)
                .ToListAsync(cancellationToken);
            foreach (var chunk in processingChunks)
            {
                chunk.Status = ChunkStatus.RetryPending;
                chunk.Error = "Recovered after the worker stopped before recording a result.";
                chunk.UpdatedAt = now;
            }

            job.Status = JobStatus.Retrying;
            job.LastActivityAt = now;
            job.UpdatedAt = now;
            logger.LogWarning("Recovered stalled translation job {JobId}; marked {ChunkCount} processing chunks retryable", job.Id, processingChunks.Count);
        }

        if (stalledJobs.Length > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<PipelineStep?> FindCurrentStepAsync(TranslationJob job, CancellationToken cancellationToken)
    {
        if (job.PipelineId is null)
        {
            return null;
        }

        var pipeline = await db.Pipelines
            .Include(item => item.Steps)
                .ThenInclude(step => step.ModelDefinition)
                    .ThenInclude(model => model!.Provider)
            .SingleOrDefaultAsync(item => item.Id == job.PipelineId, cancellationToken);
        return pipeline?.Steps
            .Where(step => step.ModelDefinition?.Provider is not null)
            .FirstOrDefault(step => string.Equals(step.ModelDefinition!.Name, job.CurrentModel, StringComparison.OrdinalIgnoreCase)
                && string.Equals(step.ModelDefinition.Provider!.Name, job.CurrentProvider, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTerminal(string status) =>
        status.Equals("completed", StringComparison.OrdinalIgnoreCase)
        || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
        || status.Equals("expired", StringComparison.OrdinalIgnoreCase)
        || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
        || status.Equals("canceled", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<Subtitle> ParseSubtitleJson(string content, IReadOnlyList<Subtitle> source)
    {
        var normalized = content.Trim();
        if (normalized.StartsWith("```", StringComparison.Ordinal))
        {
            normalized = normalized.Trim('`', ' ', '\r', '\n');
            if (normalized.StartsWith("json\n", StringComparison.OrdinalIgnoreCase)) normalized = normalized[5..];
        }

        using var document = JsonDocument.Parse(normalized);
        var sourceByIndex = source.ToDictionary(subtitle => subtitle.Index);
        var result = new List<Subtitle>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var index = item.GetProperty("index").GetInt32();
            if (!sourceByIndex.TryGetValue(index, out var original))
            {
                throw new InvalidDataException($"Azure Batch returned unknown subtitle index {index}.");
            }

            result.Add(new Subtitle
            {
                Index = index,
                Start = original.Start,
                End = original.End,
                Text = item.GetProperty("text").GetString() ?? string.Empty
            });
        }

        return result;
    }

    private static TranslationUsage ReadUsage(JsonElement body)
    {
        var usage = GetProperty(body, "usage");
        var details = GetProperty(usage, "prompt_tokens_details");
        return new TranslationUsage(
            ReadLong(usage, "prompt_tokens"),
            ReadLong(usage, "completion_tokens"),
            ReadLong(details, "cached_tokens"));
    }

    private static JsonElement GetProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static long ReadLong(JsonElement element, string name)
    {
        var value = GetProperty(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    }
}
