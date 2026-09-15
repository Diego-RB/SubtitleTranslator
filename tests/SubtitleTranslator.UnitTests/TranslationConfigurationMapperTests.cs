using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;

namespace SubtitleTranslator.UnitTests;

public class TranslationConfigurationMapperTests
{
    [Fact]
    public void Mapper_ExportsConfigurationWithoutSecrets()
    {
        var provider = new Provider
        {
            Id = 7,
            Name = "Configured provider",
            Type = ProviderType.OpenAiCompatible,
            BaseUrl = "https://example.invalid",
            AuthenticationType = AuthenticationType.ApiKey,
            CredentialId = "secret-reference"
        };

        var configuration = provider.ToConfiguration();

        Assert.Equal("OpenAiCompatible", configuration.Type);
        Assert.Equal("secret-reference", configuration.CredentialId);
        Assert.Equal("Configured provider", configuration.Name);
    }
}
