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
}
