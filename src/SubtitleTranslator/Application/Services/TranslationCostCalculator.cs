using System.Text.Json;
using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Application.Services;

public static class TranslationCostCalculator
{
    public static decimal Calculate(TranslationUsage usage, ModelDefinition model, bool isBatch = false)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(model);

        var cachedTokens = Math.Clamp(usage.CachedTokens, 0, usage.InputTokens);
        var uncachedTokens = usage.InputTokens - cachedTokens;
        var inputCost = uncachedTokens * model.InputCostPer1M;
        var cachedCost = cachedTokens * (model.CachedInputCostPer1M ?? model.InputCostPer1M);
        var outputCost = usage.OutputTokens * model.OutputCostPer1M;
        var baseCost = (inputCost + cachedCost + outputCost) / 1_000_000m;
        if (!isBatch)
        {
            return baseCost;
        }

        var discountPercent = Math.Clamp(ReadBatchDiscountPercent(model.ConfigurationJson) ?? 0m, 0m, 100m);
        return baseCost * (100m - discountPercent) / 100m;
    }

    public static decimal? ReadBatchDiscountPercent(string? configurationJson)
    {
        if (string.IsNullOrWhiteSpace(configurationJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(configurationJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("batchDiscountPercent", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetDecimal(out var discount))
                {
                    return discount;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
