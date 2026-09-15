using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Application.Interfaces;

namespace SubtitleTranslator.Application.Interfaces;

public interface ITranslationProviderFactory
{
    ITranslationProvider GetRequired(ProviderConfiguration provider);
}
