using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;
using SubtitleTranslator.Srt;
using SubtitleTranslator.Srt.Parser;
using SubtitleTranslator.Srt.Validation;
using SubtitleTranslator.Srt.Writer;

namespace SubtitleTranslator.IntegrationTests;

public class TranslationRecoveryServiceTests
{
    [Fact]
    public async Task RecoverAsync_MarksStaleProcessingChunksRetryable()
    {
        await using var db = CreateContext();
        var old = DateTimeOffset.UtcNow.AddMinutes(-30);
        var job = new TranslationJob
        {
            SourcePath = "source.srt",
            Status = JobStatus.OpenRouterProcessing,
            UpdatedAt = old,
            LastActivityAt = old,
            LastHeartbeatAt = old,
            Chunks = { new TranslationChunk { Sequence = 1, StartSubtitleIndex = 1, EndSubtitleIndex = 3, Status = ChunkStatus.Processing } }
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        var recovery = new TranslationRecoveryService(
            db,
            [],
            new AzureBatchJobStateService(db),
            new SrtFileService(new SrtParser(), new SrtWriter()),
            new SrtValidator(),
            NullLogger<TranslationRecoveryService>.Instance);

        await recovery.RecoverAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60));

        Assert.Equal(JobStatus.Retrying, job.Status);
        Assert.Equal(ChunkStatus.RetryPending, job.Chunks.Single().Status);
        Assert.Contains("Recovered", job.Chunks.Single().Error);
    }

    private static SubtitleTranslatorDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SubtitleTranslatorDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new SubtitleTranslatorDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }
}
