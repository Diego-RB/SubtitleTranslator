using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.UnitTests;

public class TranslationCostCalculatorTests
{
    [Fact]
    public void Calculate_UsesSeparateCachedInputRateAndConfiguredRates()
    {
        var model = new ModelDefinition { Name = "configured", ModelId = "model", InputCostPer1M = 2m, CachedInputCostPer1M = 0.5m, OutputCostPer1M = 10m };
        var usage = new TranslationUsage(InputTokens: 1_000_000, OutputTokens: 100_000, CachedTokens: 200_000);

        var cost = TranslationCostCalculator.Calculate(usage, model);

        Assert.Equal(2.7m, cost);
    }

    [Fact]
    public void Calculate_ClampsCachedTokensToInputTokenCount()
    {
        var model = new ModelDefinition { Name = "configured", ModelId = "model", InputCostPer1M = 1m, CachedInputCostPer1M = 0.25m };

        var cost = TranslationCostCalculator.Calculate(new TranslationUsage(InputTokens: 100, CachedTokens: 500), model);

        Assert.Equal(0.000025m, cost);
    }

    [Fact]
    public void Calculate_AppliesBatchDiscountPercent_WhenBatchIsTrue()
    {
        var model = new ModelDefinition
        {
            Name = "batch-model",
            ModelId = "gpt",
            InputCostPer1M = 2m,
            OutputCostPer1M = 10m,
            ConfigurationJson = "{\"batchDiscountPercent\":50}"
        };
        var usage = new TranslationUsage(InputTokens: 1_000_000, OutputTokens: 100_000);

        var syncCost = TranslationCostCalculator.Calculate(usage, model, isBatch: false);
        var batchCost = TranslationCostCalculator.Calculate(usage, model, isBatch: true);

        Assert.Equal(3.0m, syncCost);
        Assert.Equal(1.5m, batchCost);
    }
}
