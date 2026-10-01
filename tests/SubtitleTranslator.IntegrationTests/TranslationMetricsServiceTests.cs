using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;
using SubtitleTranslator.Workers;

namespace SubtitleTranslator.IntegrationTests;

public class TranslationMetricsServiceTests
{
    [Fact]
    public async Task RenderPrometheusAsync_ExportsPersistedAggregatesWithoutSensitiveIdentifiers()
    {
        await using var context = CreateContext();
        var provider = new Provider
        {
            Name = "Private provider name",
            Type = ProviderType.OpenRouter,
            BaseUrl = "https://private-endpoint.example",
            CredentialId = "PRIVATE_SECRET_REFERENCE",
            Enabled = true
        };
        context.Providers.Add(provider);
        var now = DateTimeOffset.UtcNow;
        context.Jobs.AddRange(
            new TranslationJob
            {
                SourcePath = "stalled-sync.srt",
                Status = JobStatus.LocalProcessing,
                UpdatedAt = now.AddMinutes(-30),
                LastActivityAt = now.AddMinutes(-30)
            },
            new TranslationJob
            {
                SourcePath = "stalled-batch.srt",
                Status = JobStatus.AzureBatchProcessing,
                UpdatedAt = now,
                LastActivityAt = now,
                LastHeartbeatAt = now,
                AzureBatchId = "batch-id",
                AzureBatchStatus = "in_progress",
                AzureBatchLastProgressAt = now.AddMinutes(-30)
            },
            new TranslationJob
            {
                SourcePath = "queued.srt",
                Status = JobStatus.Queued,
                UpdatedAt = now.AddDays(-1)
            });
        var job = new TranslationJob
        {
            SourcePath = "private-source-file.srt",
            Status = JobStatus.Completed,
            RetryCount = 2,
            EstimatedCost = 0.3m
        };
        job.Chunks.Add(new TranslationChunk
        {
            Sequence = 1,
            StartSubtitleIndex = 1,
            EndSubtitleIndex = 2,
            Status = ChunkStatus.Completed,
            Attempts =
            [
                new TranslationAttempt
                {
                    AttemptNumber = 1,
                    Provider = "Private provider name",
                    Model = "private-model-name",
                    Success = true,
                    InputTokens = 100,
                    OutputTokens = 50,
                    CachedTokens = 20,
                    CacheWriteTokens = 5,
                    LatencyMs = 1250,
                    EstimatedCost = 0.1m
                },
                new TranslationAttempt
                {
                    AttemptNumber = 2,
                    Provider = "Private provider name",
                    Model = "private-model-name",
                    Success = false,
                    InputTokens = 25,
                    OutputTokens = 0,
                    LatencyMs = 750,
                    EstimatedCost = 0.2m
                }
            ]
        });
        context.Jobs.Add(job);
        await context.SaveChangesAsync();

        var healthRegistry = new ProviderHealthRegistry();
        healthRegistry.Update(new ProviderHealthSnapshot(
            provider.Id,
            ProviderHealthState.Online,
            "Online",
            DateTimeOffset.UtcNow));
        var metrics = new TranslationMetricsService(
            context,
            healthRegistry,
            new TestOptionsMonitor<ScannerOptions>(new ScannerOptions { StalledJobThresholdMinutes = 10 }));

        var output = await metrics.RenderPrometheusAsync();

        Assert.Contains("subtitletranslator_jobs{status=\"Completed\"} 1", output);
        Assert.Contains("subtitletranslator_chunks{status=\"Completed\"} 1", output);
        Assert.Contains("subtitletranslator_translation_attempts_total{result=\"success\"} 1", output);
        Assert.Contains("subtitletranslator_translation_attempts_total{result=\"failure\"} 1", output);
        Assert.Contains("subtitletranslator_retries_total 2", output);
        Assert.Contains("subtitletranslator_input_tokens_total 125", output);
        Assert.Contains("subtitletranslator_output_tokens_total 50", output);
        Assert.Contains("subtitletranslator_cached_tokens_total 20", output);
        Assert.Contains("subtitletranslator_cache_write_tokens_total 5", output);
        Assert.Contains("subtitletranslator_attempt_latency_milliseconds_sum 2000", output);
        Assert.Contains("subtitletranslator_attempt_latency_milliseconds_count 2", output);
        Assert.Contains("subtitletranslator_estimated_cost_total 0.3", output);
        Assert.Contains("subtitletranslator_stalled_jobs 2", output);
        Assert.Contains("subtitletranslator_stalled_batches 1", output);
        Assert.Contains($"provider_id=\"{provider.Id}\",state=\"Online\"}} 1", output);
        Assert.DoesNotContain("private-source-file", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-model-name", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_SECRET_REFERENCE", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-endpoint", output, StringComparison.Ordinal);
    }

    private static SubtitleTranslatorDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SubtitleTranslatorDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new SubtitleTranslatorDbContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private sealed class TestOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
        where TOptions : class
    {
        public TOptions CurrentValue => value;
        public TOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}
