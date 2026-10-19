using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.IntegrationTests;

public class ConfigurationServicesTests
{
    [Fact]
    public async Task ProviderCanBeCreatedUpdatedAndDisabledWithoutStoringSecret()
    {
        await using var context = CreateContext();
        var service = new ProviderConfigurationService(context);
        var provider = new Provider { Name = "OpenRouter", Type = ProviderType.OpenRouter, BaseUrl = "https://openrouter.ai/api/v1", CredentialId = "OPENROUTER_API_KEY", AuthenticationType = AuthenticationType.ApiKey };

        await service.CreateAsync(provider);
        provider.Name = "OpenRouter Updated";
        provider.Enabled = false;
        await service.UpdateAsync(provider);

        var stored = await context.Providers.SingleAsync();
        Assert.Equal("OPENROUTER_API_KEY", stored.CredentialId);
        Assert.DoesNotContain("secret", stored.CredentialId, StringComparison.OrdinalIgnoreCase);
        Assert.False(stored.Enabled);
    }

    [Fact]
    public async Task ModelPersistsChunkConfigurationAndRejectsInvalidJson()
    {
        await using var context = CreateContext();
        var provider = new Provider { Name = "Ollama", Type = ProviderType.Ollama, BaseUrl = "http://localhost:11434" };
        context.Providers.Add(provider);
        await context.SaveChangesAsync();
        var service = new ModelConfigurationService(context);
        var model = new ModelDefinition { ProviderId = provider.Id, Name = "Qwen", ModelId = "qwen2.5:7b", ChunkStrategy = ChunkStrategy.MaxSubtitleCount, MaxChunkSubtitles = 1, ConfigurationJson = "{\"temperature\":0.2}" };

        await service.CreateAsync(model);
        var stored = await context.Models.SingleAsync();
        Assert.Equal(1, stored.MaxChunkSubtitles);
        Assert.Equal(ChunkStrategy.MaxSubtitleCount, stored.ChunkStrategy);

        var invalid = new ModelDefinition { ProviderId = provider.Id, Name = "Invalid", ModelId = "invalid", ConfigurationJson = "not-json" };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(invalid));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new ModelDefinition { ProviderId = 999, Name = "Missing", ModelId = "missing" }));
    }

    [Fact]
    public async Task PipelineAndStepPreserveOrderAndRejectWrongProviderModelPair()
    {
        await using var context = CreateContext();
        var providerOne = new Provider { Name = "Provider 1", Type = ProviderType.OpenRouter, BaseUrl = "https://one.example" };
        var providerTwo = new Provider { Name = "Provider 2", Type = ProviderType.Ollama, BaseUrl = "http://two.example" };
        context.Providers.AddRange(providerOne, providerTwo);
        await context.SaveChangesAsync();
        var modelOne = new ModelDefinition { ProviderId = providerOne.Id, Name = "Model 1", ModelId = "model-1" };
        var modelTwo = new ModelDefinition { ProviderId = providerTwo.Id, Name = "Model 2", ModelId = "model-2" };
        context.Models.AddRange(modelOne, modelTwo);
        await context.SaveChangesAsync();
        var pipeline = new Pipeline { Name = "Translation", Enabled = true };
        var pipelineService = new PipelineConfigurationService(context);
        await pipelineService.CreateAsync(pipeline);
        pipeline.Enabled = false;
        await pipelineService.UpdateAsync(pipeline);
        Assert.False((await context.Pipelines.SingleAsync()).Enabled);
        var stepService = new PipelineStepConfigurationService(context);
        var step = new PipelineStep { PipelineId = pipeline.Id, ModelDefinitionId = modelOne.Id, Order = 2 };

        await stepService.CreateAsync(step, providerOne.Id);
        Assert.Equal(2, (await context.PipelineSteps.SingleAsync()).Order);
        await Assert.ThrowsAsync<ArgumentException>(() => stepService.CreateAsync(new PipelineStep { PipelineId = pipeline.Id, ModelDefinitionId = modelOne.Id, Order = 1 }, providerTwo.Id));
        var secondStep = new PipelineStep { PipelineId = pipeline.Id, ModelDefinitionId = modelTwo.Id, Order = 5 };
        await stepService.CreateAsync(secondStep, providerTwo.Id);
        await stepService.MoveUpAsync(pipeline.Id, secondStep.Id);
        var orderedSteps = await context.PipelineSteps.OrderBy(item => item.Order).ToListAsync();
        Assert.Equal(secondStep.Id, orderedSteps[0].Id);
        Assert.Equal(1, orderedSteps[0].Order);
        Assert.Equal(step.Id, orderedSteps[1].Id);
        Assert.Equal(2, orderedSteps[1].Order);
        await stepService.ToggleAsync(pipeline.Id, step.Id);
        Assert.False((await context.PipelineSteps.SingleAsync(item => item.Id == step.Id)).Enabled);
    }

    private static SubtitleTranslatorDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SubtitleTranslatorDbContext>().UseSqlite("Data Source=:memory:").Options;
        var context = new SubtitleTranslatorDbContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }
}
