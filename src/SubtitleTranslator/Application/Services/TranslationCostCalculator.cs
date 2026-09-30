using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Application.Services;

public static class TranslationCostCalculator
{
    public static decimal Calculate(TranslationUsage usage, ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(model);

        var cachedTokens = Math.Clamp(usage.CachedTokens, 0, usage.InputTokens);
        var uncachedTokens = usage.InputTokens - cachedTokens;
        var inputCost = uncachedTokens * model.InputCostPer1M;
        var cachedCost = cachedTokens * (model.CachedInputCostPer1M ?? model.InputCostPer1M);
        var outputCost = usage.OutputTokens * model.OutputCostPer1M;
        return (inputCost + cachedCost + outputCost) / 1_000_000m;
    }
}
