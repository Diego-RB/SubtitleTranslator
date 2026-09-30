using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.IntegrationTests;

public class AzureBatchJobStateServiceTests
{
    [Fact]
    public async Task RecordsSubmissionAndPollingMetadataOnExistingJob()
    {
        await using var context = CreateContext();
        var job = new TranslationJob { SourcePath = "movie.srt" };
        context.Jobs.Add(job);
        await context.SaveChangesAsync();
        var service = new AzureBatchJobStateService(context);
        var createdAt = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        await service.RecordSubmissionAsync(job.Id, new BatchSubmissionResult("batch-1", "file-1", "validating", createdAt, createdAt.AddHours(24), 5, 0, 0));

        await service.RecordStatusAsync(job.Id, new BatchStatusResult("batch-1", "in_progress", "file-1", null, null, createdAt, createdAt.AddMinutes(1), createdAt.AddHours(24), null, createdAt.AddMinutes(2), 5, 2, 0, []));
        await context.Entry(job).ReloadAsync();

        Assert.Equal("batch-1", job.AzureBatchId);
        Assert.Equal("file-1", job.AzureBatchInputFileId);
        Assert.Equal("in_progress", job.AzureBatchStatus);
        Assert.Equal(5, job.AzureBatchTotalRequests);
        Assert.Equal(2, job.AzureBatchCompletedRequests);
        Assert.Equal(JobStatus.AzureBatchProcessing, job.Status);
        Assert.Equal(createdAt.AddMinutes(2), job.AzureBatchLastPolledAt);
        Assert.Equal(createdAt.AddMinutes(2), job.AzureBatchLastProgressAt);
        Assert.Equal(createdAt.AddMinutes(2), job.LastHeartbeatAt);
    }

    [Fact]
    public async Task RejectsAStatusForAnotherBatchId()
    {
        await using var context = CreateContext();
        var job = new TranslationJob { SourcePath = "movie.srt", AzureBatchId = "expected" };
        context.Jobs.Add(job);
        await context.SaveChangesAsync();
        var service = new AzureBatchJobStateService(context);
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordStatusAsync(
            job.Id, new BatchStatusResult("unexpected", "completed", "file", null, null, now, null, null, now, now, 1, 1, 0, [])));
    }

    private static SubtitleTranslatorDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SubtitleTranslatorDbContext>().UseSqlite("Data Source=:memory:").Options;
        var context = new SubtitleTranslatorDbContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }
}
