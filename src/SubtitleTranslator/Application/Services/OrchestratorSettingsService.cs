using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Application.Services;

public sealed class OrchestratorSettingsService(SubtitleTranslatorDbContext db)
{
    public async Task<OrchestratorSettings> EnsureDefaultsAsync(CancellationToken cancellationToken = default)
    {
        var promptService = new PromptConfigurationService(db);
        var defaultPromptVersion = await promptService.EnsureDefaultPromptAsync(cancellationToken);

        var settings = await db.OrchestratorSettings
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);

        if (settings is not null)
        {
            if (settings.DefaultPromptVersionId is null)
            {
                settings.DefaultPromptVersionId = defaultPromptVersion.Id;
                settings.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            return settings;
        }

        settings = new OrchestratorSettings
        {
            Id = 1,
            DefaultPromptVersionId = defaultPromptVersion.Id
        };
        db.OrchestratorSettings.Add(settings);
        await db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task<OrchestratorSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await EnsureDefaultsAsync(cancellationToken);
        return await db.OrchestratorSettings
            .Include(item => item.DefaultPipeline)
            .Include(item => item.DefaultPromptVersion)
            .ThenInclude(version => version!.PromptTemplate)
            .SingleAsync(item => item.Id == settings.Id, cancellationToken);
    }

    public async Task<List<Pipeline>> ListPipelinesAsync(CancellationToken cancellationToken = default) =>
        await db.Pipelines
            .OrderBy(pipeline => pipeline.Name)
            .ToListAsync(cancellationToken);

    public async Task<List<PromptVersion>> ListPromptVersionsAsync(CancellationToken cancellationToken = default) =>
        await db.PromptVersions
            .Include(version => version.PromptTemplate)
            .OrderBy(version => version.PromptTemplate!.Name)
            .ThenBy(version => version.Version)
            .ToListAsync(cancellationToken);

    public async Task UpdateAsync(OrchestratorSettings input, CancellationToken cancellationToken = default)
    {
        var settings = await db.OrchestratorSettings
            .SingleOrDefaultAsync(item => item.Id == (input.Id == 0 ? 1 : input.Id), cancellationToken)
            ?? await EnsureDefaultsAsync(cancellationToken);

        if (input.DefaultPipelineId is not null)
        {
            var pipelineExists = await db.Pipelines.AnyAsync(pipeline => pipeline.Id == input.DefaultPipelineId, cancellationToken);
            if (!pipelineExists)
            {
                throw new KeyNotFoundException($"Pipeline {input.DefaultPipelineId} was not found.");
            }
        }

        if (input.DefaultPromptVersionId is not null)
        {
            var promptExists = await db.PromptVersions.AnyAsync(version => version.Id == input.DefaultPromptVersionId, cancellationToken);
            if (!promptExists)
            {
                throw new KeyNotFoundException($"Prompt version {input.DefaultPromptVersionId} was not found.");
            }
        }

        settings.DefaultPipelineId = input.DefaultPipelineId;
        settings.DefaultPromptVersionId = input.DefaultPromptVersionId;
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }
}
