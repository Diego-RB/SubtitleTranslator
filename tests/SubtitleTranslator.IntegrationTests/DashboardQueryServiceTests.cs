using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.IntegrationTests;

public class DashboardQueryServiceTests
{
    [Fact]
    public async Task GetOverviewAsync_ReturnsOperationalCountsAndConfiguredProviders()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.Providers.Add(new Provider
        {
            Name = "Test provider",
            Type = ProviderType.OpenRouter,
            Enabled = true,
            Models = [new ModelDefinition { Name = "Model", ModelId = "test-model", Enabled = true }]
        });
        context.Jobs.AddRange(
            CreateJob("queued.srt", JobStatus.Queued, now),
            CreateJob("stalled.srt", JobStatus.LocalProcessing, now.AddHours(-1), lastActivity: now.AddMinutes(-30)),
            CreateJob("active.srt", JobStatus.LocalProcessing, now, lastActivity: now),
            CreateBatchJob("stalled-batch.srt", now),
            CreateJob("done.srt", JobStatus.Completed, now.AddHours(-3), completedAt: now),
            CreateJob("failed.srt", JobStatus.Failed, now.AddDays(-2)));
        await context.SaveChangesAsync();

        var service = new DashboardQueryService(context);
        var overview = await service.GetOverviewAsync(TimeSpan.FromMinutes(10));

        Assert.True(overview.IsApplicationOnline);
        Assert.Equal(4, overview.ActiveJobs);
        Assert.Equal(1, overview.QueuedJobs);
        Assert.Equal(1, overview.CompletedToday);
        Assert.Equal(1, overview.FailedJobs);
        Assert.Equal(2, overview.StalledJobs);
        var provider = Assert.Single(overview.Providers);
        Assert.Equal("Test provider", provider.Name);
        Assert.Equal(1, provider.EnabledModelCount);
        Assert.Equal(6, overview.RecentJobs.Count);
    }

    [Fact]
    public async Task GetJobsAsync_FiltersAndPaginatesJobs()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.Jobs.AddRange(
            CreateJob("/media/alpha.srt", JobStatus.Completed, now),
            CreateJob("/media/beta.srt", JobStatus.Failed, now.AddMinutes(-1)),
            CreateJob("/media/alpha-2.srt", JobStatus.Completed, now.AddMinutes(-2)));
        await context.SaveChangesAsync();

        var service = new DashboardQueryService(context);
        var result = await service.GetJobsAsync("alpha", JobStatus.Completed, page: 1, pageSize: 1);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.TotalPages);
        Assert.Single(result.Jobs);
        Assert.Equal(JobStatus.Completed, result.Jobs[0].Status);
        Assert.Equal(100, new DashboardJobRow(
            1, "done.srt", JobStatus.Completed, 0, 0, 0, null, null, now, null, null, 0m, null).ProgressPercent);
    }

    [Fact]
    public async Task GetJobAsync_LoadsPipelinePromptChunksAndAttempts()
    {
        await using var context = CreateContext();
        var prompt = new PromptTemplate { Name = "General" };
        var version = new PromptVersion { PromptTemplate = prompt, Version = 2, StaticContent = "Translate" };
        var pipeline = new Pipeline { Name = "Default" };
        var job = CreateJob("detail.srt", JobStatus.Failed, DateTimeOffset.UtcNow);
        job.Pipeline = pipeline;
        job.PromptVersion = version;
        job.Chunks.Add(new TranslationChunk
        {
            Sequence = 1,
            StartSubtitleIndex = 1,
            EndSubtitleIndex = 1,
            Status = ChunkStatus.Failed,
            Attempts =
            [
                new TranslationAttempt
                {
                    AttemptNumber = 1,
                    Provider = "Test",
                    Model = "Model",
                    Success = false,
                    ErrorCode = "temporary_error",
                    ErrorMessage = "Request failed"
                }
            ]
        });
        context.Jobs.Add(job);
        await context.SaveChangesAsync();

        var loaded = await new DashboardQueryService(context).GetJobAsync(job.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Default", loaded.Pipeline?.Name);
        Assert.Equal("General", loaded.PromptVersion?.PromptTemplate?.Name);
        var chunk = Assert.Single(loaded.Chunks);
        Assert.Equal("temporary_error", Assert.Single(chunk.Attempts).ErrorCode);
    }

    private static TranslationJob CreateJob(
        string sourcePath,
        JobStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? lastActivity = null,
        DateTimeOffset? completedAt = null) => new()
    {
        SourcePath = sourcePath,
        Status = status,
        CreatedAt = createdAt,
        UpdatedAt = lastActivity ?? createdAt,
        LastActivityAt = lastActivity,
        CompletedAt = completedAt,
        TotalChunks = status == JobStatus.Completed ? 1 : 0,
        CompletedChunks = status == JobStatus.Completed ? 1 : 0
    };

    private static TranslationJob CreateBatchJob(string sourcePath, DateTimeOffset now) => new()
    {
        SourcePath = sourcePath,
        Status = JobStatus.AzureBatchProcessing,
        CreatedAt = now,
        UpdatedAt = now,
        LastHeartbeatAt = now,
        LastActivityAt = now,
        AzureBatchId = "batch-1",
        AzureBatchStatus = "in_progress",
        AzureBatchLastProgressAt = now.AddMinutes(-30)
    };

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
}
