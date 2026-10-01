using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Application.Services;

public sealed class TranslationMetricsService(
    SubtitleTranslatorDbContext db,
    ProviderHealthRegistry providerHealthRegistry)
{
    public async Task<string> RenderPrometheusAsync(CancellationToken cancellationToken = default)
    {
        var jobCounts = await db.Jobs.AsNoTracking()
            .GroupBy(job => job.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Status, item => item.Count, cancellationToken);
        var chunkCounts = await db.Chunks.AsNoTracking()
            .GroupBy(chunk => chunk.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Status, item => item.Count, cancellationToken);
        var attemptCounts = await db.Attempts.AsNoTracking()
            .GroupBy(attempt => attempt.Success)
            .Select(group => new { Success = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Success, item => item.Count, cancellationToken);

        var inputTokens = await db.Attempts.SumAsync(attempt => attempt.InputTokens, cancellationToken);
        var outputTokens = await db.Attempts.SumAsync(attempt => attempt.OutputTokens, cancellationToken);
        var cachedTokens = await db.Attempts.SumAsync(attempt => attempt.CachedTokens ?? 0, cancellationToken);
        var cacheWriteTokens = await db.Attempts.SumAsync(attempt => attempt.CacheWriteTokens ?? 0, cancellationToken);
        var latencySum = await db.Attempts.SumAsync(attempt => attempt.LatencyMs, cancellationToken);
        var attemptCount = await db.Attempts.CountAsync(cancellationToken);
        var retries = await db.Jobs.SumAsync(job => job.RetryCount, cancellationToken);
        var estimatedCost = await db.Attempts.SumAsync(attempt => (double)attempt.EstimatedCost, cancellationToken);
        var providers = await db.Providers.AsNoTracking()
            .Select(provider => new { provider.Id, provider.Name })
            .ToListAsync(cancellationToken);

        var builder = new StringBuilder();
        AppendHeader(builder, "subtitletranslator_jobs", "Persisted translation jobs by state.", "gauge");
        foreach (var status in Enum.GetValues<JobStatus>())
        {
            jobCounts.TryGetValue(status, out var count);
            builder.Append("subtitletranslator_jobs{status=\"")
                .Append(status)
                .Append("\"} ")
                .AppendLine(count.ToString(CultureInfo.InvariantCulture));
        }

        AppendHeader(builder, "subtitletranslator_chunks", "Persisted translation chunks by state.", "gauge");
        foreach (var status in Enum.GetValues<ChunkStatus>())
        {
            chunkCounts.TryGetValue(status, out var count);
            builder.Append("subtitletranslator_chunks{status=\"")
                .Append(status)
                .Append("\"} ")
                .AppendLine(count.ToString(CultureInfo.InvariantCulture));
        }

        AppendHeader(builder, "subtitletranslator_translation_attempts_total", "Persisted translation attempts by outcome.", "counter");
        builder.Append("subtitletranslator_translation_attempts_total{result=\"success\"} ")
            .AppendLine(attemptCounts.GetValueOrDefault(true).ToString(CultureInfo.InvariantCulture));
        builder.Append("subtitletranslator_translation_attempts_total{result=\"failure\"} ")
            .AppendLine(attemptCounts.GetValueOrDefault(false).ToString(CultureInfo.InvariantCulture));

        AppendScalar(builder, "subtitletranslator_retries_total", "Persisted translation retries.", "counter", retries);
        AppendScalar(builder, "subtitletranslator_input_tokens_total", "Input tokens recorded across attempts.", "counter", inputTokens);
        AppendScalar(builder, "subtitletranslator_output_tokens_total", "Output tokens recorded across attempts.", "counter", outputTokens);
        AppendScalar(builder, "subtitletranslator_cached_tokens_total", "Cached input tokens recorded across attempts.", "counter", cachedTokens);
        AppendScalar(builder, "subtitletranslator_cache_write_tokens_total", "Cache write tokens recorded across attempts.", "counter", cacheWriteTokens);
        AppendScalar(builder, "subtitletranslator_attempt_latency_milliseconds_sum", "Total provider attempt latency in milliseconds.", "counter", latencySum);
        AppendScalar(builder, "subtitletranslator_attempt_latency_milliseconds_count", "Number of provider attempts included in latency sum.", "counter", attemptCount);
        AppendScalar(builder, "subtitletranslator_estimated_cost_total", "Estimated cost summed over persisted attempts in configured currency units.", "counter", estimatedCost);

        AppendHeader(builder, "subtitletranslator_provider_health", "Current in-memory provider health state (one means active state).", "gauge");
        foreach (var provider in providers)
        {
            var state = providerHealthRegistry.Get(provider.Id).State;
            foreach (var healthState in Enum.GetValues<ProviderHealthState>())
            {
                builder.Append("subtitletranslator_provider_health{provider_id=\"")
                    .Append(provider.Id.ToString(CultureInfo.InvariantCulture))
                    .Append("\",state=\"")
                    .Append(healthState)
                    .Append("\"} ")
                    .AppendLine((state == healthState ? 1 : 0).ToString(CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static void AppendScalar<T>(StringBuilder builder, string name, string help, string type, T value)
        where T : IFormattable
    {
        AppendHeader(builder, name, help, type);
        builder.Append(name).Append(' ')
            .AppendLine(value.ToString(null, CultureInfo.InvariantCulture));
    }

    private static void AppendHeader(StringBuilder builder, string name, string help, string type)
    {
        builder.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
        builder.Append("# TYPE ").Append(name).Append(' ').AppendLine(type);
    }
}
