using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Application.Interfaces;

namespace SubtitleTranslator.Application.Services;

public sealed class TranslationProviderFactory(
    IEnumerable<ITranslationProvider> providers) : ITranslationProviderFactory
{
    private readonly IReadOnlyDictionary<string, ITranslationProvider> providers =
        providers.ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);

    public ITranslationProvider GetRequired(ProviderConfiguration provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (string.IsNullOrWhiteSpace(provider.Type))
        {
            throw new InvalidOperationException($"Provider '{provider.Name}' has no type configured.");
        }

        if (!providers.TryGetValue(provider.Type, out var translationProvider))
        {
            throw new InvalidOperationException(
                $"No translation adapter is registered for provider type '{provider.Type}'.");
        }

        return translationProvider;
    }
}
