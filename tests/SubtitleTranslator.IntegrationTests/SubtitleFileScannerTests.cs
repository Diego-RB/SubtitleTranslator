using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.IntegrationTests;

public class SubtitleFileScannerTests
{
    [Fact]
    public async Task ScanOnce_DiscoversNestedSourceFilesOnceAndIgnoresTranslatedOutputs()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"scanner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "season-1"));
        await File.WriteAllTextAsync(Path.Combine(directory, "season-1", "episode.srt"), "source");
        await File.WriteAllTextAsync(Path.Combine(directory, "episode.pt-BR.srt"), "translated");
        await File.WriteAllTextAsync(Path.Combine(directory, "episode.pt.srt"), "translated");
        try
        {
            await using var db = CreateContext();
            var pipeline = new Pipeline { Name = "Default" };
            var prompt = new PromptVersion { PromptTemplate = new PromptTemplate { Name = "Default" }, Version = 1, StaticContent = "Translate." };
            db.Add(new OrchestratorSettings { Id = 1, DefaultPipeline = pipeline, DefaultPromptVersion = prompt });
            await db.SaveChangesAsync();
            var scanner = new SubtitleFileScanner(db, NullLogger<SubtitleFileScanner>.Instance);

            var firstScan = await scanner.ScanOnceAsync([directory]);
            var secondScan = await scanner.ScanOnceAsync([directory]);
            var job = await db.Jobs.SingleAsync();

            Assert.Equal(1, firstScan);
            Assert.Equal(0, secondScan);
            Assert.EndsWith("episode.srt", job.SourcePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(pipeline.Id, job.PipelineId);
            Assert.Equal(prompt.Id, job.PromptVersionId);
            Assert.Equal(JobStatus.Discovered, job.Status);
            Assert.EndsWith("episode.pt-BR.srt", job.OutputPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanOnce_RequeuesCompletedJobWhenOutputWasDeletedAndSourceIsUnchanged()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"scanner-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "episode.srt");
        var outputPath = Path.Combine(directory, "episode.pt-BR.srt");
        await File.WriteAllTextAsync(sourcePath, "source");
        try
        {
            await using var db = CreateContext();
            var completedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(sourcePath).AddMinutes(1), TimeSpan.Zero);
            var job = new TranslationJob
            {
                SourcePath = OperatingSystem.IsWindows() ? Path.GetFullPath(sourcePath).ToUpperInvariant() : Path.GetFullPath(sourcePath),
                OutputPath = outputPath,
                Status = JobStatus.Completed,
                CompletedAt = completedAt,
                CompletedChunks = 1,
                TotalChunks = 1
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            var scanner = new SubtitleFileScanner(db, NullLogger<SubtitleFileScanner>.Instance);

            var firstScan = await scanner.ScanOnceAsync([directory]);
            var secondScan = await scanner.ScanOnceAsync([directory]);

            Assert.Equal(1, firstScan);
            Assert.Equal(0, secondScan);
            Assert.Equal(JobStatus.Discovered, job.Status);
            Assert.Null(job.CompletedAt);
            Assert.Single(await db.Jobs.ToListAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanOnce_DoesNotRequeueCompletedJobWhenOutputExistsOrSourceChanged()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"scanner-protect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "episode.srt");
        var outputPath = Path.Combine(directory, "episode.pt-BR.srt");
        await File.WriteAllTextAsync(sourcePath, "source");
        await File.WriteAllTextAsync(outputPath, "translated");
        try
        {
            await using var db = CreateContext();
            var completedAt = DateTimeOffset.UtcNow;
            var job = new TranslationJob
            {
                SourcePath = OperatingSystem.IsWindows() ? Path.GetFullPath(sourcePath).ToUpperInvariant() : Path.GetFullPath(sourcePath),
                OutputPath = outputPath,
                Status = JobStatus.Completed,
                CompletedAt = completedAt
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            var scanner = new SubtitleFileScanner(db, NullLogger<SubtitleFileScanner>.Instance);

            Assert.Equal(0, await scanner.ScanOnceAsync([directory]));
            File.Delete(outputPath);
            File.SetLastWriteTimeUtc(sourcePath, completedAt.AddMinutes(1).UtcDateTime);

            Assert.Equal(0, await scanner.ScanOnceAsync([directory]));
            Assert.Equal(JobStatus.Completed, job.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
