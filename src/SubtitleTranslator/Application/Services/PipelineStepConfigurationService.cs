using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Application.Services;

public sealed class PipelineStepConfigurationService(SubtitleTranslatorDbContext db)
{
    public Task<Pipeline?> GetPipelineAsync(int pipelineId, CancellationToken cancellationToken = default) =>
        db.Pipelines.SingleOrDefaultAsync(pipeline => pipeline.Id == pipelineId, cancellationToken);

    public Task<PipelineStep?> GetAsync(int pipelineId, int id, CancellationToken cancellationToken = default) =>
        db.PipelineSteps.Include(step => step.Pipeline).Include(step => step.ModelDefinition).ThenInclude(model => model!.Provider)
            .SingleOrDefaultAsync(step => step.PipelineId == pipelineId && step.Id == id, cancellationToken);

    public Task<List<Domain.Entities.Provider>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
        db.Providers.Where(provider => provider.Enabled).OrderBy(provider => provider.Name).ToListAsync(cancellationToken);

    public Task<List<ModelDefinition>> GetModelsAsync(CancellationToken cancellationToken = default) =>
        db.Models.Include(model => model.Provider).Where(model => model.Enabled && model.Provider!.Enabled)
            .OrderBy(model => model.Provider!.Name).ThenBy(model => model.Name).ToListAsync(cancellationToken);

    public async Task CreateAsync(PipelineStep step, int providerId, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(step, providerId, cancellationToken);
        db.PipelineSteps.Add(step);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(PipelineStep input, int providerId, CancellationToken cancellationToken = default)
    {
        var step = await db.PipelineSteps.SingleOrDefaultAsync(item => item.Id == input.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"Pipeline step {input.Id} was not found.");
        if (step.PipelineId != input.PipelineId)
        {
            throw new ArgumentException("The step does not belong to the selected pipeline.", nameof(input));
        }
        await ValidateAsync(input, providerId, cancellationToken);
        step.PipelineId = input.PipelineId;
        step.ModelDefinitionId = input.ModelDefinitionId;
        step.Order = input.Order;
        step.Enabled = input.Enabled;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ToggleAsync(int pipelineId, int id, CancellationToken cancellationToken = default)
    {
        var step = await GetAsync(pipelineId, id, cancellationToken)
            ?? throw new KeyNotFoundException($"Pipeline step {id} was not found.");
        step.Enabled = !step.Enabled;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int pipelineId, int id, CancellationToken cancellationToken = default)
    {
        var step = await db.PipelineSteps.SingleOrDefaultAsync(item => item.Id == id && item.PipelineId == pipelineId, cancellationToken)
            ?? throw new KeyNotFoundException($"Pipeline step {id} was not found.");
        db.PipelineSteps.Remove(step);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task MoveUpAsync(int pipelineId, int id, CancellationToken cancellationToken = default) =>
        MoveAsync(pipelineId, id, -1, cancellationToken);

    public Task MoveDownAsync(int pipelineId, int id, CancellationToken cancellationToken = default) =>
        MoveAsync(pipelineId, id, 1, cancellationToken);

    private async Task MoveAsync(int pipelineId, int id, int direction, CancellationToken cancellationToken)
    {
        var steps = await db.PipelineSteps
            .Where(step => step.PipelineId == pipelineId)
            .OrderBy(step => step.Order)
            .ThenBy(step => step.Id)
            .ToListAsync(cancellationToken);
        var currentIndex = steps.FindIndex(step => step.Id == id);
        if (currentIndex < 0)
        {
            throw new KeyNotFoundException($"Pipeline step {id} was not found.");
        }

        var targetIndex = currentIndex + direction;
        if (targetIndex < 0 || targetIndex >= steps.Count)
        {
            return;
        }

        (steps[currentIndex], steps[targetIndex]) = (steps[targetIndex], steps[currentIndex]);
        for (var index = 0; index < steps.Count; index++)
        {
            steps[index].Order = index + 1;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ValidateAsync(PipelineStep step, int? providerId, CancellationToken cancellationToken)
    {
        if (step.PipelineId <= 0 || !await db.Pipelines.AnyAsync(pipeline => pipeline.Id == step.PipelineId, cancellationToken))
        {
            throw new ArgumentException("A valid pipeline is required.", nameof(step));
        }

        if (step.Order < 1)
        {
            throw new ArgumentException("Step order must be at least 1.", nameof(step));
        }

        var model = await db.Models.Include(item => item.Provider)
            .SingleOrDefaultAsync(item => item.Id == step.ModelDefinitionId, cancellationToken);
        if (model is null)
        {
            throw new ArgumentException("A valid model is required.", nameof(step));
        }

        if (model.Provider is null || model.ProviderId != model.Provider.Id)
        {
            throw new ArgumentException("The selected model has an invalid provider.", nameof(step));
        }

        if (providerId.HasValue && model.ProviderId != providerId.Value)
        {
            throw new ArgumentException("The selected model does not belong to the selected provider.", nameof(step));
        }
    }
}
