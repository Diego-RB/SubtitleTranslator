using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Application.Services;

public sealed class TranslationJobRetryService(SubtitleTranslatorDbContext db)
{
    public async Task<bool> RetryAsync(int jobId, CancellationToken cancellationToken = default)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null
            || job.Status is not (JobStatus.Failed or JobStatus.Cancelled)
            || !File.Exists(job.SourcePath)
            || job.PipelineId is null
            || job.PromptVersionId is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        job.Status = JobStatus.Discovered;
        job.CompletedAt = null;
        job.FailedChunks = 0;
        job.CurrentProvider = null;
        job.CurrentModel = null;
        job.AzureBatchId = null;
        job.AzureBatchStatus = null;
        job.AzureBatchInputFileId = null;
        job.AzureBatchOutputFileId = null;
        job.AzureBatchErrorFileId = null;
        job.AzureBatchCreatedAt = null;
        job.AzureBatchInProgressAt = null;
        job.AzureBatchExpiresAt = null;
        job.AzureBatchCompletedAt = null;
        job.AzureBatchLastPolledAt = null;
        job.AzureBatchLastProgressAt = null;
        job.AzureBatchTotalRequests = null;
        job.AzureBatchCompletedRequests = null;
        job.AzureBatchFailedRequests = null;
        job.LastHeartbeatAt = null;
        job.LastActivityAt = now;
        job.LastError = null;
        job.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}