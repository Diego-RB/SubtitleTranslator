using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Application.Interfaces;
using SubtitleTranslator.Application.Services;
using SubtitleTranslator.Domain.ValueObjects;

namespace SubtitleTranslator.UnitTests;

public class TranslationProviderFactoryTests
{
    [Fact]
    public void GetRequired_SelectsAdapterByPersistedProviderType()
    {
        var adapter = new FakeProvider("OpenRouter");
        var factory = new TranslationProviderFactory([adapter]);
        var configuration = new ProviderConfiguration(
            1,
            "Configured OpenRouter",
            "OpenRouter",
            "https://example.invalid",
            "ApiKey",
            "credential-ref",
            null);

        var selected = factory.GetRequired(configuration);

        Assert.Same(adapter, selected);
    }

    [Fact]
    public void GetRequired_ResolvesOpenAiCompatibleToOpenRouterAdapter()
    {
        var adapter = new FakeProvider("OpenRouter");
        var factory = new TranslationProviderFactory([adapter]);
        var configuration = new ProviderConfiguration(
            2,
            "Compatible Endpoint",
            "OpenAiCompatible",
            "https://compatible.example/v1",
            "ApiKey",
            "COMPATIBLE_KEY",
            null);

        var selected = factory.GetRequired(configuration);

        Assert.Same(adapter, selected);
    }

    [Fact]
    public void GetRequired_ThrowsWhenAdapterIsNotRegistered()
    {
        var factory = new TranslationProviderFactory([]);
        var configuration = new ProviderConfiguration(
            1, "Local", "Ollama", null, "None", null, null);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            factory.GetRequired(configuration));

        Assert.Contains("Ollama", exception.Message);
    }

    private sealed class FakeProvider(string name) : ITranslationProvider
    {
        public string Name { get; } = name;

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationResult(request.Subtitles, new TranslationUsage()));
    }
}
