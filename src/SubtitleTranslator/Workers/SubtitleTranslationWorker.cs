using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Workers;

public sealed class SubtitleTranslationWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ScannerOptions> options,
    WorkerRuntimeStatus runtimeStatus,
    ILogger<SubtitleTranslationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        runtimeStatus.MarkOnline();
        try
        {
            var nextScan = DateTimeOffset.MinValue;
            var nextRecovery = DateTimeOffset.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                var configuration = options.CurrentValue;
                var now = DateTimeOffset.UtcNow;
                if (configuration.Enabled && now >= nextScan)
                {
                    await RunScannerAsync(configuration.Directories, stoppingToken);
                    nextScan = now.AddSeconds(Math.Max(1, configuration.IntervalSeconds));
                }

                if (now >= nextRecovery)
                {
                    await RunRecoveryAsync(configuration, stoppingToken);
                    nextRecovery = now.AddSeconds(Math.Max(1, configuration.RecoveryIntervalSeconds));
                }

                var jobId = await TryClaimNextJobAsync(
                    TimeSpan.FromMinutes(Math.Max(1, configuration.StalledJobThresholdMinutes)),
                    TimeSpan.FromSeconds(Math.Max(0, configuration.FileStabilityDelaySeconds)),
                    stoppingToken);
                if (jobId is not null)
                {
                    await ExecuteClaimedJobAsync(jobId.Value, configuration, stoppingToken);
                    continue;
                }

                var delaySeconds = Math.Min(
                    Math.Max(1, configuration.IntervalSeconds),
                    Math.Max(1, configuration.RecoveryIntervalSeconds));
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
        }
        finally
        {
            runtimeStatus.MarkOffline();
        }
    }

    private async Task RunScannerAsync(IReadOnlyCollection<string> directories, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var scanner = scope.ServiceProvider.GetRequiredService<SubtitleFileScanner>();
            var discovered = await scanner.ScanOnceAsync(directories, cancellationToken);
            if (discovered > 0)
            {
                logger.LogInformation("Subtitle scanner discovered or re-queued {JobCount} files", discovered);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Subtitle scanner cycle failed");
        }
    }

    private async Task RunRecoveryAsync(ScannerOptions configuration, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var recovery = scope.ServiceProvider.GetRequiredService<TranslationRecoveryService>();
            await recovery.RecoverAsync(
                TimeSpan.FromMinutes(Math.Max(1, configuration.StalledJobThresholdMinutes)),
                TimeSpan.FromSeconds(Math.Max(1, configuration.RecoveryIntervalSeconds)),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Translation recovery cycle failed");
        }
    }

    private async Task<int?> TryClaimNextJobAsync(
        TimeSpan staleAfter,
        TimeSpan fileStabilityDelay,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SubtitleTranslatorDbContext>();
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now - staleAfter;
        var settings = await db.OrchestratorSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        var pipelineId = settings?.DefaultPipelineId;
        var promptVersionId = settings?.DefaultPromptVersionId;
        var statuses = new[]
        {
            JobStatus.Discovered,
            JobStatus.Queued,
            JobStatus.Retrying,
            JobStatus.Preparing,
            JobStatus.OpenRouterProcessing,
            JobStatus.AzureOpenAiProcessing,
            JobStatus.LocalProcessing,
            JobStatus.Validating
        };
        var pendingCandidates = await db.Jobs.AsNoTracking()
            .Where(job => statuses.Contains(job.Status)
                && (job.PipelineId != null || pipelineId != null)
                && (job.PromptVersionId != null || promptVersionId != null))
            .OrderBy(job => job.Id)
            .Select(job => new
            {
                job.Id,
                job.SourcePath,
                job.Status,
                job.CreatedAt,
                job.LastHeartbeatAt,
                job.LastActivityAt,
                job.UpdatedAt,
                job.AzureBatchId,
                job.AzureBatchStatus
            })
            .Take(20)
            .ToListAsync(cancellationToken);
        var candidates = pendingCandidates
            .Where(candidate =>
                IsSourceFileStable(candidate.SourcePath, now, fileStabilityDelay)
                && (candidate.AzureBatchId is null || IsTerminalBatch(candidate.AzureBatchStatus))
                && (candidate.Status is JobStatus.Discovered or JobStatus.Queued
                    || (candidate.LastHeartbeatAt ?? candidate.LastActivityAt ?? candidate.UpdatedAt) <= staleBefore))
            .OrderBy(candidate => candidate.CreatedAt)
            .ToArray();

        foreach (var candidate in candidates)
        {
            var update = db.Jobs.Where(job => job.Id == candidate.Id
                && job.Status == candidate.Status
                && job.AzureBatchId == candidate.AzureBatchId
                && job.AzureBatchStatus == candidate.AzureBatchStatus);
            update = candidate.LastHeartbeatAt is null
                ? update.Where(job => job.LastHeartbeatAt == null)
                : update.Where(job => job.LastHeartbeatAt == candidate.LastHeartbeatAt);
            var claimed = await update.ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, JobStatus.Preparing)
                    .SetProperty(job => job.PipelineId, job => job.PipelineId ?? pipelineId)
                    .SetProperty(job => job.PromptVersionId, job => job.PromptVersionId ?? promptVersionId)
                    .SetProperty(job => job.LastHeartbeatAt, now)
                    .SetProperty(job => job.LastActivityAt, now)
                    .SetProperty(job => job.UpdatedAt, now), cancellationToken);
            if (claimed == 1)
            {
                return candidate.Id;
            }
        }

        return null;
    }

    private static bool IsSourceFileStable(string sourcePath, DateTimeOffset now, TimeSpan stabilityDelay)
    {
        try
        {
            return !File.Exists(sourcePath)
                || File.GetLastWriteTimeUtc(sourcePath) <= (now - stabilityDelay).UtcDateTime;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsTerminalBatch(string? status) =>
        status is not null
        && (status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("expired", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase));

    private async Task ExecuteClaimedJobAsync(
        int jobId,
        ScannerOptions configuration,
        CancellationToken stoppingToken)
    {
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeatTask = MaintainHeartbeatAsync(
            jobId,
            TimeSpan.FromSeconds(Math.Max(5, configuration.HeartbeatIntervalSeconds)),
            heartbeatCancellation.Token);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var executor = scope.ServiceProvider.GetRequiredService<TranslationPipelineExecutor>();
            var result = await executor.ExecuteJobAsync(jobId, stoppingToken);
            if (!result.Succeeded)
            {
                logger.LogWarning("Translation job {JobId} ended with {FailedSubtitleCount} failed subtitle(s)", jobId, result.FailedSubtitles);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Translation job {JobId} could not be executed", jobId);
            await MarkFailedAsync(jobId, exception, stoppingToken);
        }
        finally
        {
            await heartbeatCancellation.CancelAsync();
            try { await heartbeatTask; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task MaintainHeartbeatAsync(
        int jobId,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<SubtitleTranslatorDbContext>();
                var now = DateTimeOffset.UtcNow;
                await db.Jobs.Where(job => job.Id == jobId && job.Status != JobStatus.Completed && job.Status != JobStatus.Cancelled)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LastHeartbeatAt, now), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not update heartbeat for job {JobId}", jobId);
            }
        }
    }

    private async Task MarkFailedAsync(int jobId, Exception exception, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SubtitleTranslatorDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.Jobs.Where(job => job.Id == jobId && job.Status != JobStatus.Completed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, JobStatus.Failed)
                .SetProperty(job => job.LastError, exception.Message)
                .SetProperty(job => job.LastActivityAt, now)
                .SetProperty(job => job.UpdatedAt, now), cancellationToken);
    }
}
