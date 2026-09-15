using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Application.Interfaces;
using SubtitleTranslator.Domain.ValueObjects;
using SubtitleTranslator.Infrastructure.Providers.OpenRouter;

namespace SubtitleTranslator.UnitTests;

public class OpenRouterProviderTests
{
    [Fact]
    public async Task TranslateAsync_SendsOpenAiCompatiblePayloadAndPreservesTimestamps()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            capturedRequest = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new
            {
                id = "or-request-1",
                choices = new[] { new { message = new { role = "assistant", content = "[{\"index\":2,\"text\":\"Mundo\"},{\"index\":1,\"text\":\"Olá\"}]" } } },
                usage = new
                {
                    prompt_tokens = 30,
                    completion_tokens = 12,
                    prompt_tokens_details = new { cached_tokens = 7 }
                }
            });
        }));
        var provider = new OpenRouterProvider(
            httpClient,
            new FakeCredentialResolver("openrouter-secret"),
            NullLogger<OpenRouterProvider>.Instance);

        var result = await provider.TranslateAsync(Request(Subtitles()));

        Assert.Equal("or-request-1", result.ProviderRequestId);
        Assert.Equal(30, result.Usage.InputTokens);
        Assert.Equal(12, result.Usage.OutputTokens);
        Assert.Equal(7, result.Usage.CachedTokens);
        Assert.Equal([1, 2], result.Subtitles.Select(subtitle => subtitle.Index));
        Assert.Equal(Subtitles()[0].Start, result.Subtitles[0].Start);
        Assert.Equal("Olá", result.Subtitles[0].Text);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://router.test/api/v1/chat/completions", capturedRequest.RequestUri!.ToString());
        Assert.Equal("Bearer openrouter-secret", capturedRequest.Headers.Authorization!.ToString());
        Assert.Equal("https://subtitletranslator.test", capturedRequest.Headers.GetValues("HTTP-Referer").Single());
        Assert.Equal("SubtitleTranslator", capturedRequest.Headers.GetValues("X-Title").Single());

        var body = JsonSerializer.Deserialize<JsonElement>(capturedBody!);
        Assert.Equal("google/gemma", body.GetProperty("model").GetString());
        Assert.Equal("translate", body.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Contains("pt-BR", body.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TranslateAsync_RetriesRateLimitAndSucceeds()
    {
        var calls = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : JsonResponse(new
                {
                    choices = new[] { new { message = new { content = "[{\"index\":1,\"text\":\"Olá\"}]" } } },
                    usage = new { prompt_tokens = 1, completion_tokens = 1 }
                });
        }));
        var provider = new OpenRouterProvider(
            httpClient,
            new FakeCredentialResolver("secret"),
            NullLogger<OpenRouterProvider>.Instance);
        var request = Request([Subtitles()[0]]) with
        {
            Model = Request([Subtitles()[0]]).Model with
            {
                ConfigurationJson = "{\"maxRetries\":1,\"retryDelayMilliseconds\":0}"
            }
        };

        var result = await provider.TranslateAsync(request);

        Assert.Equal(2, calls);
        Assert.Equal("Olá", result.Subtitles.Single().Text);
    }

    [Fact]
    public async Task TranslateAsync_DoesNotExposeCredentialOnPermanentFailure()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("private upstream response")
            }));
        var provider = new OpenRouterProvider(
            httpClient,
            new FakeCredentialResolver("super-secret"),
            NullLogger<OpenRouterProvider>.Instance);
        var request = Request([Subtitles()[0]]) with
        {
            Model = Request([Subtitles()[0]]).Model with
            {
                ConfigurationJson = "{\"maxRetries\":0}"
            }
        };

        var exception = await Assert.ThrowsAsync<OpenRouterProviderException>(() =>
            provider.TranslateAsync(request));

        Assert.Equal(502, exception.StatusCode);
        Assert.DoesNotContain("super-secret", exception.Message);
        Assert.DoesNotContain("private upstream response", exception.Message);
        Assert.True(exception.IsTransient);
    }

    private static TranslationRequest Request(IReadOnlyList<Subtitle> subtitles) => new()
    {
        Provider = new ProviderConfiguration(
            1,
            "OpenRouter",
            "OpenRouter",
            "https://router.test/api/v1",
            "ApiKey",
            "OPENROUTER_API_KEY",
            null),
        Model = new ModelConfiguration(
            1,
            "Gemma",
            "google/gemma",
            null,
            null,
            512,
            0.2,
            0.9,
            "MaxTokens",
            8000,
            null,
            "{\"httpReferer\":\"https://subtitletranslator.test\",\"xTitle\":\"SubtitleTranslator\"}"),
        Subtitles = subtitles,
        Prompt = "translate"
    };

    private static Subtitle[] Subtitles() =>
    [
        new() { Index = 1, Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2), Text = "Hello" },
        new() { Index = 2, Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(4), Text = "World" }
    ];

    private static HttpResponseMessage JsonResponse(object value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class FakeCredentialResolver(string value) : ICredentialResolver
    {
        public string? Resolve(string? credentialReference) => value;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
