using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Application.Services;

public sealed class AzureBatchJobStateService(SubtitleTranslatorDbContext db)
{
    public async Task RecordSubmissionAsync(
        int jobId,
        BatchSubmissionResult submission,
        CancellationToken cancellationToken = default)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(job.AzureBatchId)
            && !IsTerminal(job.AzureBatchStatus))
        {
            throw new InvalidOperationException($"Job {jobId} already has Azure Batch '{job.AzureBatchId}'.");
        }

        job.AzureBatchId = submission.BatchId;
        job.AzureBatchInputFileId = submission.InputFileId;
        job.AzureBatchOutputFileId = null;
        job.AzureBatchErrorFileId = null;
        job.AzureBatchInProgressAt = null;
        job.AzureBatchCompletedAt = null;
        job.AzureBatchStatus = submission.Status;
        job.AzureBatchCreatedAt = submission.CreatedAt;
        job.AzureBatchExpiresAt = submission.ExpiresAt;
        job.AzureBatchTotalRequests = submission.TotalRequests;
        job.AzureBatchCompletedRequests = submission.CompletedRequests;
        job.AzureBatchFailedRequests = submission.FailedRequests;
        job.AzureBatchLastProgressAt = submission.CreatedAt;
        job.Status = JobStatus.AzureBatchSubmitting;
        Touch(job, submission.CreatedAt);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordStatusAsync(
        int jobId,
        BatchStatusResult status,
        CancellationToken cancellationToken = default)
    {
        var job = await GetJobAsync(jobId, cancellationToken);
        if (!string.Equals(job.AzureBatchId, status.BatchId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Batch '{status.BatchId}' does not match the batch attached to job {jobId}.");
        }

        var previousProgress = (job.AzureBatchTotalRequests, job.AzureBatchCompletedRequests, job.AzureBatchFailedRequests);
        var previousStatus = job.AzureBatchStatus;
        var polledAt = status.LastPolledAt ?? DateTimeOffset.UtcNow;
        job.AzureBatchStatus = status.Status;
        job.AzureBatchInputFileId = status.InputFileId;
        job.AzureBatchOutputFileId = status.OutputFileId;
        job.AzureBatchErrorFileId = status.ErrorFileId;
        job.AzureBatchCreatedAt = status.CreatedAt;
        job.AzureBatchInProgressAt = status.InProgressAt;
        job.AzureBatchExpiresAt = status.ExpiresAt;
        job.AzureBatchCompletedAt = status.CompletedAt;
        job.AzureBatchLastPolledAt = polledAt;
        job.AzureBatchTotalRequests = status.TotalRequests;
        job.AzureBatchCompletedRequests = status.CompletedRequests;
        job.AzureBatchFailedRequests = status.FailedRequests;
        job.LastHeartbeatAt = polledAt;

        var currentProgress = (status.TotalRequests, status.CompletedRequests, status.FailedRequests);
        if (previousProgress != currentProgress || !string.Equals(previousStatus, status.Status, StringComparison.OrdinalIgnoreCase))
        {
            job.AzureBatchLastProgressAt = polledAt;
        }

        job.Status = MapJobStatus(status.Status, job.Status);
        Touch(job, polledAt);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<TranslationJob> GetJobAsync(int jobId, CancellationToken cancellationToken) =>
        await db.Jobs.SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken)
        ?? throw new KeyNotFoundException($"Translation job {jobId} was not found.");

    private static JobStatus MapJobStatus(string batchStatus, JobStatus current) =>
        batchStatus.ToLowerInvariant() switch
        {
            "validating" => JobStatus.AzureBatchSubmitting,
            "in_progress" => JobStatus.AzureBatchProcessing,
            "finalizing" => JobStatus.AzureBatchFinalizing,
            "completed" => JobStatus.Validating,
            "failed" or "expired" => JobStatus.Failed,
            "cancelling" => JobStatus.AzureBatchProcessing,
            "cancelled" or "canceled" => JobStatus.Cancelled,
            _ => current
        };

    private static bool IsTerminal(string? status) =>
        status is not null
        && (status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("expired", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase));

    private static void Touch(TranslationJob job, DateTimeOffset at)
    {
        job.UpdatedAt = at;
        job.LastActivityAt = at;
    }
}
