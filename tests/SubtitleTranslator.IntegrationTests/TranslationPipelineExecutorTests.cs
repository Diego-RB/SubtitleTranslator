using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Application.Interfaces;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Domain.ValueObjects;
using SubtitleTranslator.Infrastructure.Persistence;
using SubtitleTranslator.Srt;
using SubtitleTranslator.Srt.Chunking;
using SubtitleTranslator.Srt.Parser;
using SubtitleTranslator.Srt.Validation;
using SubtitleTranslator.Srt.Writer;

namespace SubtitleTranslator.IntegrationTests;

public class TranslationPipelineExecutorTests
{
    [Fact]
    public async Task ExecuteJob_FallsBackOnlyForPendingSubtitlesAndUsesNextModelsChunkPolicy()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"source-{Guid.NewGuid():N}.srt");
        var outputPath = Path.Combine(Path.GetTempPath(), $"output-{Guid.NewGuid():N}.srt");
        await File.WriteAllTextAsync(sourcePath,
            "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nWorld\r\n");
        try
        {
            await using var db = CreateContext();
            var cloudProvider = new Provider { Name = "Cloud", Type = ProviderType.OpenRouter, BaseUrl = "https://cloud.example" };
            var localProvider = new Provider { Name = "Local", Type = ProviderType.Ollama, BaseUrl = "http://local.example" };
            var cloudModel = new ModelDefinition { Name = "Cloud model", ModelId = "cloud-model", Provider = cloudProvider, ChunkStrategy = ChunkStrategy.MaxSubtitleCount, MaxChunkSubtitles = 2, SupportsTranslation = true, ConfigurationJson = "{\"pipelineMaxAttempts\":2,\"pipelineRetryDelayMilliseconds\":0}" };
            var localModel = new ModelDefinition { Name = "Local model", ModelId = "local-model", Provider = localProvider, ChunkStrategy = ChunkStrategy.MaxSubtitleCount, MaxChunkSubtitles = 1, SupportsTranslation = true };
            var template = new PromptTemplate { Name = "Translation" };
            var prompt = new PromptVersion { PromptTemplate = template, Version = 1, StaticContent = "Translate naturally." };
            var pipeline = new Pipeline
            {
                Name = "Fallback pipeline",
                Steps =
                {
                    new PipelineStep { Order = 1, ModelDefinition = cloudModel },
                    new PipelineStep { Order = 2, ModelDefinition = localModel }
                }
            };
            var job = new TranslationJob { SourcePath = sourcePath, OutputPath = outputPath, Pipeline = pipeline, PromptVersion = prompt };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();

            var cloud = new FakeTranslationProvider("OpenRouter", (_, _) => throw new HttpRequestException("cloud unavailable"));
            var local = new FakeTranslationProvider("Ollama", (request, _) => Task.FromResult(new TranslationResult(
                request.Subtitles.Select(subtitle => new Subtitle
                {
                    Index = subtitle.Index,
                    Start = subtitle.Start,
                    End = subtitle.End,
                    Text = $"pt-{subtitle.Text}"
                }).ToArray(),
                new TranslationUsage(InputTokens: 10, OutputTokens: 8, LatencyMs: 4))));
            var chunker = new SrtChunker();
            var service = new TranslationPipelineExecutor(
                db,
                new PipelineDefinitionService(db),
                new TranslationProviderFactory([cloud, local]),
                [],
                new AzureBatchJobStateService(db),
                new SrtFileService(new SrtParser(), new SrtWriter()),
                chunker,
                new SrtValidator(),
                NullLogger<TranslationPipelineExecutor>.Instance);

            var result = await service.ExecuteJobAsync(job.Id);

            Assert.True(result.Succeeded);
            Assert.Equal(2, result.CompletedSubtitles);
            Assert.Equal(4, result.Attempts);
            Assert.Equal([2, 2, 1, 1], cloud.RequestSizes.Concat(local.RequestSizes));
            Assert.Equal(JobStatus.Completed, job.Status);
            Assert.Equal(1, job.CompletedChunks);
            Assert.Equal(2, job.RetryCount);
            Assert.True(File.Exists(outputPath));
            var output = new SrtParser().Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal("pt-Hello", output[0].Text);
            Assert.Equal(TimeSpan.FromSeconds(1), output[0].Start);
            Assert.Equal("pt-World", output[1].Text);
            Assert.Equal(4, await db.Attempts.CountAsync());
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task ExecuteJob_FailsWithoutWritingOutputWhenAllStepsFail()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"source-{Guid.NewGuid():N}.srt");
        var outputPath = Path.Combine(Path.GetTempPath(), $"output-{Guid.NewGuid():N}.srt");
        await File.WriteAllTextAsync(sourcePath, "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        try
        {
            await using var db = CreateContext();
            var provider = new Provider { Name = "Cloud", Type = ProviderType.OpenRouter, BaseUrl = "https://cloud.example" };
            var model = new ModelDefinition { Name = "Model", ModelId = "model", Provider = provider, ChunkStrategy = ChunkStrategy.MaxSubtitleCount, MaxChunkSubtitles = 1 };
            var prompt = new PromptVersion { PromptTemplate = new PromptTemplate { Name = "Prompt" }, Version = 1, StaticContent = "Translate." };
            var pipeline = new Pipeline { Name = "Pipeline", Steps = { new PipelineStep { Order = 1, ModelDefinition = model } } };
            var job = new TranslationJob { SourcePath = sourcePath, OutputPath = outputPath, Pipeline = pipeline, PromptVersion = prompt };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            var broken = new FakeTranslationProvider("OpenRouter", (_, _) => Task.FromResult(new TranslationResult([], new TranslationUsage())));
            var service = new TranslationPipelineExecutor(db, new PipelineDefinitionService(db), new TranslationProviderFactory([broken]), [], new AzureBatchJobStateService(db), new SrtFileService(new SrtParser(), new SrtWriter()), new SrtChunker(), new SrtValidator(), NullLogger<TranslationPipelineExecutor>.Instance);

            var result = await service.ExecuteJobAsync(job.Id);

            Assert.False(result.Succeeded);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.False(File.Exists(outputPath));
            Assert.Equal(1, job.FailedChunks);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task ExecuteJob_UsesConfiguredBatchProviderAndPersistsBatchState()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"source-{Guid.NewGuid():N}.srt");
        var outputPath = Path.Combine(Path.GetTempPath(), $"output-{Guid.NewGuid():N}.srt");
        await File.WriteAllTextAsync(sourcePath, "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        try
        {
            await using var db = CreateContext();
            var provider = new Provider { Name = "Azure", Type = ProviderType.AzureOpenAi, BaseUrl = "https://azure.example", AuthenticationType = AuthenticationType.ApiKey, CredentialId = "AZURE_OPENAI_KEY" };
            var model = new ModelDefinition { Name = "Azure batch", ModelId = "model", DeploymentName = "deployment", Provider = provider, SupportsBatch = true, ChunkStrategy = ChunkStrategy.MaxSubtitleCount, MaxChunkSubtitles = 1, ConfigurationJson = "{\"batchPollIntervalSeconds\":0}" };
            var prompt = new PromptVersion { PromptTemplate = new PromptTemplate { Name = "Prompt" }, Version = 1, StaticContent = "Translate." };
            var pipeline = new Pipeline { Name = "Batch pipeline", Steps = { new PipelineStep { Order = 1, ModelDefinition = model } } };
            var job = new TranslationJob { SourcePath = sourcePath, OutputPath = outputPath, Pipeline = pipeline, PromptVersion = prompt };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            var batch = new FakeBatchProvider();
            var executor = new TranslationPipelineExecutor(db, new PipelineDefinitionService(db), new TranslationProviderFactory([]), [batch], new AzureBatchJobStateService(db), new SrtFileService(new SrtParser(), new SrtWriter()), new SrtChunker(), new SrtValidator(), NullLogger<TranslationPipelineExecutor>.Instance);

            var result = await executor.ExecuteJobAsync(job.Id);

            Assert.True(result.Succeeded);
            Assert.Equal("batch-42", job.AzureBatchId);
            Assert.Equal("completed", job.AzureBatchStatus);
            Assert.Equal("file-in", job.AzureBatchInputFileId);
            Assert.Equal("file-out", job.AzureBatchOutputFileId);
            Assert.Equal("job-1-chunk-1", Assert.Single(batch.SubmittedItems).CustomId);
            Assert.Equal("Olá", new SrtParser().Parse(await File.ReadAllTextAsync(outputPath)).Single().Text);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task ExecuteJob_AfterRestartReusesPersistedCompletedChunkResults()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"source-{Guid.NewGuid():N}.srt");
        var outputPath = Path.Combine(Path.GetTempPath(), $"output-{Guid.NewGuid():N}.srt");
        await File.WriteAllTextAsync(sourcePath,
            "1\n00:00:01,000 --> 00:00:02,000\nFirst\n\n2\n00:00:03,000 --> 00:00:04,000\nSecond\n");
        try
        {
            await using var db = CreateContext();
            var provider = new Provider { Name = "OpenRouter", Type = ProviderType.OpenRouter, BaseUrl = "https://cloud.example" };
            var model = new ModelDefinition { Name = "Model", ModelId = "model", Provider = provider, ChunkStrategy = ChunkStrategy.MaxSubtitleCount, MaxChunkSubtitles = 1 };
            var prompt = new PromptVersion { PromptTemplate = new PromptTemplate { Name = "Prompt" }, Version = 1, StaticContent = "Translate." };
            var pipeline = new Pipeline { Name = "Pipeline", Steps = { new PipelineStep { Order = 1, ModelDefinition = model } } };
            var job = new TranslationJob { SourcePath = sourcePath, OutputPath = outputPath, Pipeline = pipeline, PromptVersion = prompt };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();

            using var cancelled = new CancellationTokenSource();
            var firstRunProvider = new FakeTranslationProvider("OpenRouter", (request, _) =>
            {
                if (request.Subtitles.Single().Index == 1)
                {
                    return Task.FromResult(Translation(request.Subtitles.Single(), "translated-first"));
                }

                cancelled.Cancel();
                throw new OperationCanceledException(cancelled.Token);
            });
            var firstExecutor = CreateExecutor(db, [firstRunProvider]);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstExecutor.ExecuteJobAsync(job.Id, cancelled.Token));
            Assert.Equal(ChunkStatus.Completed, (await db.Chunks.OrderBy(chunk => chunk.Sequence).FirstAsync()).Status);
            Assert.NotNull((await db.Chunks.OrderBy(chunk => chunk.Sequence).FirstAsync()).ResultJson);

            job.Status = JobStatus.Retrying;
            await db.SaveChangesAsync();
            var afterRestartProvider = new FakeTranslationProvider("OpenRouter", (request, _) =>
                Task.FromResult(Translation(request.Subtitles.Single(), "translated-second")));
            var resumed = await CreateExecutor(db, [afterRestartProvider]).ExecuteJobAsync(job.Id);

            Assert.True(resumed.Succeeded);
            Assert.Equal([2], afterRestartProvider.RequestedSubtitleIndexes);
            var output = new SrtParser().Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal("translated-first", output[0].Text);
            Assert.Equal("translated-second", output[1].Text);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    private static TranslationResult Translation(Subtitle source, string text) =>
        new([new Subtitle { Index = source.Index, Start = source.Start, End = source.End, Text = text }], new TranslationUsage());

    private static TranslationPipelineExecutor CreateExecutor(
        SubtitleTranslatorDbContext db,
        IEnumerable<ITranslationProvider> providers) =>
        new(db, new PipelineDefinitionService(db), new TranslationProviderFactory(providers), [], new AzureBatchJobStateService(db), new SrtFileService(new SrtParser(), new SrtWriter()), new SrtChunker(), new SrtValidator(), NullLogger<TranslationPipelineExecutor>.Instance);

    private static SubtitleTranslatorDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SubtitleTranslatorDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new SubtitleTranslatorDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    private sealed class FakeTranslationProvider(
        string name,
        Func<TranslationRequest, CancellationToken, Task<TranslationResult>> translate) : ITranslationProvider
    {
        public string Name { get; } = name;
        public List<int> RequestSizes { get; } = [];
        public List<int> RequestedSubtitleIndexes { get; } = [];
        public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
        {
            RequestSizes.Add(request.Subtitles.Count);
            RequestedSubtitleIndexes.AddRange(request.Subtitles.Select(subtitle => subtitle.Index));
            return await translate(request, cancellationToken);
        }
    }

    private sealed class FakeBatchProvider : IBatchTranslationProvider
    {
        public string Name => "AzureOpenAi";
        public IReadOnlyList<BatchTranslationItem> SubmittedItems { get; private set; } = [];
        private readonly DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1760000000);

        public Task<BatchSubmissionResult> SubmitBatchAsync(ProviderConfiguration provider, ModelConfiguration model, string prompt, IReadOnlyList<BatchTranslationItem> items, CancellationToken cancellationToken = default)
        {
            SubmittedItems = items;
            return Task.FromResult(new BatchSubmissionResult("batch-42", "file-in", "completed", now, now.AddHours(24), items.Count, items.Count, 0));
        }

        public Task<BatchStatusResult> GetStatusAsync(ProviderConfiguration provider, string batchId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BatchStatusResult(batchId, "completed", "file-in", "file-out", "file-error", now, now, now.AddHours(24), now, now, 1, 1, 0, []));

        public Task<BatchResult> GetResultsAsync(ProviderConfiguration provider, string batchId, CancellationToken cancellationToken = default)
        {
            using var document = JsonDocument.Parse("{\"choices\":[{\"message\":{\"content\":\"[{\\\"index\\\":1,\\\"text\\\":\\\"Olá\\\"}]\"}}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":7}}");
            return Task.FromResult(new BatchResult([new BatchResultLine(SubmittedItems.Single().CustomId, 200, document.RootElement.Clone(), null)]));
        }

        public Task<BatchErrorResult> GetErrorsAsync(ProviderConfiguration provider, string batchId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BatchErrorResult([]));

        public Task CancelBatchAsync(ProviderConfiguration provider, string batchId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
