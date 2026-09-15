using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Application.Services;

public static class TranslationConfigurationMapper
{
    public static ProviderConfiguration ToConfiguration(this Provider provider) =>
        new(
            provider.Id,
            provider.Name,
            provider.Type.ToString(),
            provider.BaseUrl,
            provider.AuthenticationType.ToString(),
            provider.CredentialId,
            provider.ApiVersion);

    public static ModelConfiguration ToConfiguration(this ModelDefinition model) =>
        new(
            model.Id,
            model.Name,
            model.ModelId,
            model.DeploymentName,
            model.ContextWindow,
            model.MaxOutputTokens,
            model.Temperature,
            model.TopP,
            model.ChunkStrategy.ToString(),
            model.MaxChunkTokens,
            model.MaxChunkSubtitles,
            model.ConfigurationJson);
}
