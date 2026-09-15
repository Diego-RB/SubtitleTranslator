using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Application.Interfaces;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Application.Services;

public sealed class PipelineDefinitionService(SubtitleTranslatorDbContext db)
    : IPipelineDefinitionService
{
    public async Task<Pipeline> GetRequiredAsync(
        int pipelineId,
        CancellationToken cancellationToken = default)
    {
        var pipeline = await db.Pipelines
            .Include(item => item.Steps)
                .ThenInclude(step => step.ModelDefinition)
                    .ThenInclude(model => model!.Provider)
            .SingleOrDefaultAsync(item => item.Id == pipelineId, cancellationToken);

        return pipeline ?? throw new KeyNotFoundException($"Pipeline {pipelineId} was not found.");
    }

    public async Task<IReadOnlyList<PipelineStep>> GetEnabledStepsAsync(
        int pipelineId,
        CancellationToken cancellationToken = default)
    {
        var pipeline = await GetRequiredAsync(pipelineId, cancellationToken);
        if (!pipeline.Enabled)
        {
            return [];
        }

        return pipeline.Steps
            .Where(step => step.Enabled
                && step.ModelDefinition is { Enabled: true, SupportsTranslation: true }
                && step.ModelDefinition.Provider is { Enabled: true })
            .OrderBy(step => step.Order)
            .ThenBy(step => step.Id)
            .ToArray();
    }
}
