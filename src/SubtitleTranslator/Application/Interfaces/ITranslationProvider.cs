using SubtitleTranslator.Application.Contracts;

namespace SubtitleTranslator.Application.Interfaces;

public interface ITranslationProvider
{
    string Name { get; }

    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);
}
