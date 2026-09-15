using Microsoft.EntityFrameworkCore;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Infrastructure.Persistence;

namespace SubtitleTranslator.IntegrationTests;

public class PipelineDefinitionServiceTests
{
    [Fact]
    public async Task GetEnabledSteps_ReturnsOnlyEnabledDefinitionsInOrder()
    {
        await using var context = CreateContext();
        var enabledProvider = new Provider
        {
            Name = "Enabled provider",
            Type = ProviderType.OpenRouter,
            AuthenticationType = AuthenticationType.ApiKey
        };
        var disabledProvider = new Provider
        {
            Name = "Disabled provider",
            Type = ProviderType.Ollama,
            AuthenticationType = AuthenticationType.None,
            Enabled = false
        };
        var firstModel = new ModelDefinition
        {
            Name = "First",
            ModelId = "first",
            Provider = enabledProvider,
            Priority = 10
        };
        var secondModel = new ModelDefinition
        {
            Name = "Second",
            ModelId = "second",
            Provider = enabledProvider,
            Priority = 20
        };
        var disabledModel = new ModelDefinition
        {
            Name = "Disabled",
            ModelId = "disabled",
            Provider = disabledProvider
        };
        var pipeline = new Pipeline
        {
            Name = "Configured pipeline",
            Steps =
            {
                new PipelineStep { Order = 20, ModelDefinition = firstModel },
                new PipelineStep { Order = 10, ModelDefinition = secondModel },
                new PipelineStep { Order = 30, ModelDefinition = disabledModel },
                new PipelineStep { Order = 40, ModelDefinition = firstModel, Enabled = false }
            }
        };

        context.Pipelines.Add(pipeline);
        await context.SaveChangesAsync();

        var service = new PipelineDefinitionService(context);
        var steps = await service.GetEnabledStepsAsync(pipeline.Id);

        Assert.Equal(["Second", "First"], steps.Select(step => step.ModelDefinition!.Name));
    }

    [Fact]
    public async Task GetEnabledSteps_ReturnsEmptyForDisabledPipeline()
    {
        await using var context = CreateContext();
        var pipeline = new Pipeline { Name = "Disabled", Enabled = false };
        context.Pipelines.Add(pipeline);
        await context.SaveChangesAsync();

        var steps = await new PipelineDefinitionService(context).GetEnabledStepsAsync(pipeline.Id);

        Assert.Empty(steps);
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
