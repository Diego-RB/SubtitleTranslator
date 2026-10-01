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

    private static SubtitleTranslatorDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SubtitleTranslatorDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new SubtitleTranslatorDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }
}
