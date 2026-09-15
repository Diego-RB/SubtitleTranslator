using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.IntegrationTests;

public class SubtitleTranslatorDbContextTests
{
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

    [Fact]
    public async Task CanPersistAndRetrieveJobWithChunks()
    {
        await using var context = CreateContext();

        var job = new TranslationJob
        {
            SourcePath = "/media/streaming/show.s01e01.srt",
            TotalChunks = 1,
            Chunks =
            {
                new TranslationChunk
                {
                    Sequence = 1,
                    StartSubtitleIndex = 1,
                    EndSubtitleIndex = 50
                }
            }
        };

        context.Jobs.Add(job);
        await context.SaveChangesAsync();

        var reloaded = await context.Jobs
            .Include(j => j.Chunks)
            .SingleAsync(j => j.Id == job.Id);

        Assert.Equal(JobStatus.Discovered, reloaded.Status);
        Assert.Single(reloaded.Chunks);
        Assert.Equal(ChunkStatus.Pending, reloaded.Chunks.Single().Status);
    }

    [Fact]
    public async Task CanPersistProviderWithModel()
    {
        await using var context = CreateContext();

        var provider = new Provider
        {
            Name = "OpenRouter",
            Type = ProviderType.OpenRouter,
            AuthenticationType = AuthenticationType.ApiKey,
            Models =
            {
                new ModelDefinition
                {
                    Name = "Gemma",
                    ModelId = "google/gemma-2-9b-it",
                    ProviderId = 0
                }
            }
        };

        context.Providers.Add(provider);
        await context.SaveChangesAsync();

        var reloaded = await context.Providers
            .Include(p => p.Models)
            .SingleAsync(p => p.Name == "OpenRouter");

        Assert.Single(reloaded.Models);
        Assert.Equal("google/gemma-2-9b-it", reloaded.Models.Single().ModelId);
    }
}
