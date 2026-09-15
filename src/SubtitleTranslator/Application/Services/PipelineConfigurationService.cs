using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Application.Services;

public sealed class PipelineConfigurationService(SubtitleTranslatorDbContext db)
{
    public Task<List<Pipeline>> ListAsync(CancellationToken cancellationToken = default) =>
        db.Pipelines.Include(pipeline => pipeline.Steps).OrderBy(pipeline => pipeline.Name).ToListAsync(cancellationToken);

    public Task<Pipeline?> GetAsync(int id, CancellationToken cancellationToken = default) =>
        db.Pipelines.Include(pipeline => pipeline.Steps).ThenInclude(step => step.ModelDefinition)
            .ThenInclude(model => model!.Provider).SingleOrDefaultAsync(pipeline => pipeline.Id == id, cancellationToken);

    public async Task CreateAsync(Pipeline pipeline, CancellationToken cancellationToken = default)
    {
        Validate(pipeline);
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Pipeline input, CancellationToken cancellationToken = default)
    {
        var pipeline = await db.Pipelines.SingleOrDefaultAsync(item => item.Id == input.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"Pipeline {input.Id} was not found.");
        Validate(input);
        pipeline.Name = input.Name;
        pipeline.Description = input.Description;
        pipeline.Enabled = input.Enabled;
        pipeline.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ToggleAsync(int id, CancellationToken cancellationToken = default)
    {
        var pipeline = await db.Pipelines.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Pipeline {id} was not found.");
        pipeline.Enabled = !pipeline.Enabled;
        pipeline.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var pipeline = await db.Pipelines.Include(item => item.Steps)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Pipeline {id} was not found.");
        if (await db.Jobs.AnyAsync(job => job.PipelineId == id, cancellationToken))
        {
            throw new InvalidOperationException("Pipeline cannot be deleted because translation jobs reference it. Disable it instead.");
        }

        db.Pipelines.Remove(pipeline);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void Validate(Pipeline pipeline)
    {
        if (string.IsNullOrWhiteSpace(pipeline.Name))
        {
            throw new ArgumentException("Pipeline name is required.", nameof(pipeline));
        }
    }
}
