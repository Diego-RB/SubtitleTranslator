using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Domain.ValueObjects;
using SubtitleTranslator.Infrastructure.Providers.Ollama;

namespace SubtitleTranslator.UnitTests;

public class OllamaProviderTests
{
    [Fact]
    public async Task TranslateAsync_SendsConfiguredRequestAndPreservesTimestamps()
    {
        HttpRequestMessage? capturedRequest = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            capturedRequest = request;
            return JsonResponse(new
            {
                response = "[{\"index\":2,\"text\":\"Mundo\"},{\"index\":1,\"text\":\"Olá\"}]",
                prompt_eval_count = 12,
                eval_count = 8
            });
        }));
        var provider = new OllamaProvider(httpClient, NullLogger<OllamaProvider>.Instance);
        var source = Subtitles();

        var result = await provider.TranslateAsync(Request(source));

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("http://ollama.test/api/generate", capturedRequest.RequestUri!.ToString());
        var body = await capturedRequest.Content!.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("qwen2.5:7b", body.GetProperty("model").GetString());
        var prompt = body.GetProperty("prompt").GetString();
        Assert.Contains("translate the subtitles", prompt);
        Assert.Contains("Hello", prompt);
        Assert.Contains("World", prompt);
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.Equal(12, result.Usage.InputTokens);
        Assert.Equal(8, result.Usage.OutputTokens);
        Assert.Equal([1, 2], result.Subtitles.Select(subtitle => subtitle.Index));
        Assert.Equal(source[0].Start, result.Subtitles[0].Start);
        Assert.Equal(source[1].End, result.Subtitles[1].End);
        Assert.Equal("Olá", result.Subtitles[0].Text);
    }

    [Fact]
    public async Task TranslateAsync_UsesModelConfigurationJson()
    {
        HttpRequestMessage? capturedRequest = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            capturedRequest = request;
            return JsonResponse(new { response = "[{\"index\":1,\"text\":\"Olá\"}]" });
        }));
        var provider = new OllamaProvider(httpClient, NullLogger<OllamaProvider>.Instance);
        var request = Request([Subtitles()[0]]) with
        {
            Model = Request([Subtitles()[0]]).Model with
            {
                ConfigurationJson = "{\"timeoutSeconds\":15,\"maxParallelRequests\":1,\"numPredict\":256,\"temperature\":0.2}"
            }
        };

        await provider.TranslateAsync(request);

        var body = await capturedRequest!.Content!.ReadFromJsonAsync<JsonElement>();
        var options = body.GetProperty("options");
        Assert.Equal(256, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(0.2, options.GetProperty("temperature").GetDouble(), precision: 5);
    }

    [Fact]
    public async Task TranslateAsync_RejectsInvalidTranslatedStructure()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            JsonResponse(new { response = "[{\"index\":1,\"text\":\"Olá\"}]" })));
        var provider = new OllamaProvider(httpClient, NullLogger<OllamaProvider>.Instance);

        var exception = await Assert.ThrowsAsync<OllamaProviderException>(() =>
            provider.TranslateAsync(Request(Subtitles())));

        Assert.Contains("expected 2", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranslateAsync_ThrowsOnHttpErrorWithoutLeakingResponseBody()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("secret provider details")
            }));
        var provider = new OllamaProvider(httpClient, NullLogger<OllamaProvider>.Instance);

        var exception = await Assert.ThrowsAsync<OllamaProviderException>(() =>
            provider.TranslateAsync(Request(Subtitles())));

        Assert.Equal(503, exception.StatusCode);
        Assert.DoesNotContain("secret provider details", exception.Message);
    }

    private static TranslationRequest Request(IReadOnlyList<Subtitle> subtitles) => new()
    {
        Provider = new ProviderConfiguration(1, "Local Ollama", "Ollama", "http://ollama.test", "None", null, null),
        Model = new ModelConfiguration(1, "Qwen", "qwen2.5:7b", null, null, 512, 0.7, 0.9, "MaxSubtitleCount", null, 1, null),
        Subtitles = subtitles,
        Prompt = "translate the subtitles"
    };

    private static Subtitle[] Subtitles() =>
    [
        new() { Index = 1, Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2), Text = "Hello" },
        new() { Index = 2, Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(4), Text = "World" }
    ];

    private static HttpResponseMessage JsonResponse(object value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
