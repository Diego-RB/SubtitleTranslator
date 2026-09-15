using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.Application.Services;

public sealed class ModelConfigurationService(SubtitleTranslatorDbContext db)
{
    public Task<List<ModelDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
        db.Models.Include(model => model.Provider).OrderBy(model => model.Provider!.Name).ThenBy(model => model.Name)
            .ToListAsync(cancellationToken);

    public Task<ModelDefinition?> GetAsync(int id, CancellationToken cancellationToken = default) =>
        db.Models.Include(model => model.Provider).SingleOrDefaultAsync(model => model.Id == id, cancellationToken);

    public Task<List<Domain.Entities.Provider>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
        db.Providers.Where(provider => provider.Enabled).OrderBy(provider => provider.Name).ToListAsync(cancellationToken);

    public async Task CreateAsync(ModelDefinition model, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(model, cancellationToken);
        db.Models.Add(model);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(ModelDefinition input, CancellationToken cancellationToken = default)
    {
        var model = await db.Models.SingleOrDefaultAsync(item => item.Id == input.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"Model {input.Id} was not found.");
        await ValidateAsync(input, cancellationToken);
        model.ProviderId = input.ProviderId;
        model.Name = input.Name;
        model.ModelId = input.ModelId;
        model.DeploymentName = input.DeploymentName;
        model.Enabled = input.Enabled;
        model.Priority = input.Priority;
        model.SupportsBatch = input.SupportsBatch;
        model.SupportsStreaming = input.SupportsStreaming;
        model.SupportsTranslation = input.SupportsTranslation;
        model.ContextWindow = input.ContextWindow;
        model.MaxOutputTokens = input.MaxOutputTokens;
        model.Temperature = input.Temperature;
        model.TopP = input.TopP;
        model.InputCostPer1M = input.InputCostPer1M;
        model.OutputCostPer1M = input.OutputCostPer1M;
        model.CachedInputCostPer1M = input.CachedInputCostPer1M;
        model.ChunkStrategy = input.ChunkStrategy;
        model.MaxChunkTokens = input.MaxChunkTokens;
        model.MaxChunkSubtitles = input.MaxChunkSubtitles;
        model.ConfigurationJson = input.ConfigurationJson;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ToggleAsync(int id, CancellationToken cancellationToken = default)
    {
        var model = await db.Models.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Model {id} was not found.");
        model.Enabled = !model.Enabled;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var model = await db.Models.Include(item => item.Provider).SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Model {id} was not found.");
        if (await db.PipelineSteps.AnyAsync(step => step.ModelDefinitionId == id, cancellationToken))
        {
            throw new InvalidOperationException("Model cannot be deleted while pipeline steps reference it. Disable it instead.");
        }

        db.Models.Remove(model);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ValidateAsync(ModelDefinition model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
        {
            throw new ArgumentException("Model name is required.", nameof(model));
        }

        if (string.IsNullOrWhiteSpace(model.ModelId))
        {
            throw new ArgumentException("Model ID is required.", nameof(model));
        }

        if (model.MaxChunkSubtitles < 0 || model.MaxChunkTokens < 0 || model.MaxOutputTokens < 0)
        {
            throw new ArgumentException("Chunk and output limits cannot be negative.", nameof(model));
        }

        if (!string.IsNullOrWhiteSpace(model.ConfigurationJson))
        {
            try
            {
                JsonDocument.Parse(model.ConfigurationJson);
            }
            catch (JsonException exception)
            {
                throw new ArgumentException("Configuration JSON is invalid.", nameof(model), exception);
            }
        }

        if (!await db.Providers.AnyAsync(provider => provider.Id == model.ProviderId, cancellationToken))
        {
            throw new ArgumentException("A valid provider is required.", nameof(model));
        }
    }
}
