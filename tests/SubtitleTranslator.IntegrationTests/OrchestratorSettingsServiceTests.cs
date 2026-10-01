using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.IntegrationTests;

public class OrchestratorSettingsServiceTests
{
    [Fact]
    public async Task EnsureDefaultsAsync_CreatesRecord_WhenMissing()
    {
        await using var context = CreateContext();
        var service = new OrchestratorSettingsService(context);

        var settings = await service.EnsureDefaultsAsync();

        Assert.Equal(1, settings.Id);
        Assert.Null(settings.DefaultPipelineId);
        Assert.Null(settings.DefaultPromptVersionId);
    }

    [Fact]
    public async Task UpdateAsync_StoresSelectedPipelineAndPromptVersion()
    {
        await using var context = CreateContext();
        var pipeline = new Pipeline { Name = "Primary", Enabled = true };
        var template = new PromptTemplate { Name = "Generic" };
        var version = new PromptVersion { PromptTemplate = template, Name = "v1", StaticContent = "Translate" };
        context.Pipelines.Add(pipeline);
        context.PromptTemplates.Add(template);
        context.PromptVersions.Add(version);
        await context.SaveChangesAsync();

        var service = new OrchestratorSettingsService(context);
        await service.EnsureDefaultsAsync();
        await service.UpdateAsync(new OrchestratorSettings
        {
            Id = 1,
            DefaultPipelineId = pipeline.Id,
            DefaultPromptVersionId = version.Id,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var stored = await context.OrchestratorSettings.Include(x => x.DefaultPipeline).Include(x => x.DefaultPromptVersion).SingleAsync();
        Assert.Equal(pipeline.Id, stored.DefaultPipelineId);
        Assert.Equal(version.Id, stored.DefaultPromptVersionId);
    }

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
