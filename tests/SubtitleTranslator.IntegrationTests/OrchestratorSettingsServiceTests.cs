using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.IntegrationTests;

public class OrchestratorSettingsServiceTests
{
    [Fact]
    public async Task EnsureDefaultsAsync_CreatesRecordAndDefaultPrompt_WhenMissing()
    {
        await using var context = CreateContext();
        var service = new OrchestratorSettingsService(context);

        var settings = await service.EnsureDefaultsAsync();

        Assert.Equal(1, settings.Id);
        Assert.Null(settings.DefaultPipelineId);
        Assert.NotNull(settings.DefaultPromptVersionId);
        var seededVersion = await context.PromptVersions
            .Include(version => version.PromptTemplate)
            .SingleAsync(version => version.Id == settings.DefaultPromptVersionId);
        Assert.Equal(PromptConfigurationService.DefaultTemplateName, seededVersion.PromptTemplate!.Name);
        Assert.True(seededVersion.Enabled);
    }

    [Fact]
    public async Task PromptConfigurationService_CreatesAndUpdatesVersionedPrompts()
    {
        await using var context = CreateContext();
        var service = new PromptConfigurationService(context);

        var template = await service.CreateTemplateAsync(new PromptTemplate { Name = "Custom", Description = "Test" });
        var v1 = await service.CreateVersionAsync(new PromptVersion
        {
            PromptTemplateId = template.Id,
            Name = "Base",
            StaticContent = "Translate to pt-BR",
            GlossaryJson = "{\"AI\":\"IA\"}",
            SupportsPromptCache = true,
            PromptCacheKey = "cache-v1"
        });
        var v2 = await service.CreateVersionAsync(new PromptVersion
        {
            PromptTemplateId = template.Id,
            Name = "Tuned",
            StaticContent = "Translate naturally to pt-BR"
        });

        Assert.Equal(1, v1.Version);
        Assert.Equal(2, v2.Version);

        v1.Name = "Base Updated";
        v1.SupportsPromptCache = false;
        await service.UpdateVersionAsync(v1);
        await service.ToggleVersionAsync(v2.Id);

        var loaded = await service.GetTemplateAsync(template.Id);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Versions.Count);
        Assert.False(loaded.Versions.Single(item => item.Id == v2.Id).Enabled);
        Assert.Equal("Base Updated", loaded.Versions.Single(item => item.Id == v1.Id).Name);
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
