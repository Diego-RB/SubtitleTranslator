using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Application.Interfaces;
using SubtitleTranslator.Domain.ValueObjects;
using SubtitleTranslator.Infrastructure.Providers.AzureOpenAi;

namespace SubtitleTranslator.UnitTests;

public class AzureOpenAiProviderTests
{
    [Fact]
    public async Task TranslateAsync_UsesDeploymentApiVersionAndApiKeyHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            capturedRequest = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new
            {
                id = "azure-request-1",
                choices = new[] { new { message = new { role = "assistant", content = "[{\"index\":2,\"text\":\"Mundo\"},{\"index\":1,\"text\":\"Olá\"}]" } } },
                usage = new { prompt_tokens = 20, completion_tokens = 9, prompt_tokens_details = new { cached_tokens = 4, cache_write_tokens = 12 } }
            });
        }));
        var provider = CreateProvider(client, "azure-secret");

        var result = await provider.TranslateAsync(Request() with { SupportsPromptCache = true, PromptCacheKey = "cache-v1" });

        Assert.Equal("azure-request-1", result.ProviderRequestId);
        Assert.Equal(20, result.Usage.InputTokens);
        Assert.Equal(9, result.Usage.OutputTokens);
        Assert.Equal(4, result.Usage.CachedTokens);
        Assert.Equal(12, result.Usage.CacheWriteTokens);
        Assert.Equal("https://resource.openai.azure.com/openai/deployments/gpt-deployment/chat/completions?api-version=2025-01-01-preview", capturedRequest!.RequestUri!.ToString());
        Assert.Equal("azure-secret", capturedRequest.Headers.GetValues("api-key").Single());
        var body = JsonSerializer.Deserialize<JsonElement>(capturedBody!);
        Assert.Equal("cache-v1", body.GetProperty("prompt_cache_key").GetString());
        Assert.Contains("translate", body.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Contains("Hello", body.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal("Olá", result.Subtitles[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Subtitles[0].Start);
    }

    [Fact]
    public async Task TranslateAsync_RetriesTransientStatusAndSucceeds()
    {
        var calls = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : JsonResponse(new { choices = new[] { new { message = new { content = "[{\"index\":1,\"text\":\"Olá\"}]" } } } });
        }));
        var provider = CreateProvider(client, "secret");
        var request = Request() with
        {
            Subtitles = [Request().Subtitles[0]],
            Model = Request().Model with { ConfigurationJson = "{\"maxRetries\":1,\"retryDelayMilliseconds\":0}" }
        };

        var result = await provider.TranslateAsync(request);

        Assert.Equal(2, calls);
        Assert.Equal("Olá", result.Subtitles.Single().Text);
    }

    [Fact]
    public async Task TranslateAsync_RequiresDeploymentAndDoesNotExposeCredential()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("private error") }));
        var provider = CreateProvider(client, "super-secret");
        var request = Request() with { Model = Request().Model with { DeploymentName = null } };

        var validation = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranslateAsync(request));
        Assert.Contains("DeploymentName", validation.Message);

        var failing = new AzureOpenAiProvider(client, new FakeCredentialResolver("super-secret"), NullLogger<AzureOpenAiProvider>.Instance);
        var exception = await Assert.ThrowsAsync<AzureOpenAiProviderException>(() => failing.TranslateAsync(Request() with { Model = Request().Model with { ConfigurationJson = "{\"maxRetries\":0}" } }));
        Assert.Equal(400, exception.StatusCode);
        Assert.DoesNotContain("super-secret", exception.Message);
        Assert.DoesNotContain("private error", exception.Message);
    }

    private static AzureOpenAiProvider CreateProvider(HttpClient client, string credential) =>
        new(client, new FakeCredentialResolver(credential), NullLogger<AzureOpenAiProvider>.Instance);

    private static TranslationRequest Request() => new()
    {
        Provider = new ProviderConfiguration(1, "Azure", "AzureOpenAi", "https://resource.openai.azure.com", "ApiKey", "AZURE_OPENAI_KEY", "2025-01-01-preview"),
        Model = new ModelConfiguration(1, "GPT", "gpt-4o-mini", "gpt-deployment", null, 512, 0.2, 0.9, "MaxTokens", 8000, null, null),
        Subtitles = [new Subtitle { Index = 1, Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2), Text = "Hello" }, new Subtitle { Index = 2, Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(4), Text = "World" }],
        Prompt = "translate"
    };

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class FakeCredentialResolver(string value) : ICredentialResolver
    {
        public string? Resolve(string? credentialReference) => value;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
