using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Application.Interfaces;
using SubtitleTranslator.Domain.ValueObjects;
using SubtitleTranslator.Infrastructure.Providers.AzureBatch;

namespace SubtitleTranslator.UnitTests;

public class AzureBatchProviderTests
{
    [Fact]
    public async Task SubmitBatchAsync_UploadsUtf8JsonlAndCreatesBatch()
    {
        var requests = new List<(HttpMethod Method, string Url, string? Body, string? ApiKey)>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.ToString(), request.Content?.ReadAsStringAsync().GetAwaiter().GetResult(), request.Headers.GetValues("api-key").Single()));
            return requests.Count == 1
                ? JsonResponse(new { id = "file-123", created_at = 1 })
                : JsonResponse(new { id = "batch-456", status = "validating", input_file_id = "file-123", created_at = 1760000000, expires_at = 1760086400, request_counts = new { total = 2, completed = 0, failed = 0 } });
        }));
        var provider = CreateProvider(client);

        var result = await provider.SubmitBatchAsync(
            ProviderConfig(), ModelConfig(), "static translation prompt",
            [new BatchTranslationItem("chunk-1", Subtitles(), SupportsPromptCache: true, PromptCacheKey: "batch-cache-v1")]);

        Assert.Equal("batch-456", result.BatchId);
        Assert.Equal("file-123", result.InputFileId);
        Assert.Equal(2, requests.Count);
        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.EndsWith("/openai/v1/files", requests[0].Url);
        Assert.Contains("purpose", requests[0].Body);
        Assert.Contains("batch", requests[0].Body);
        Assert.Contains("chunk-1", requests[0].Body);
        Assert.Contains("deployment-x", requests[0].Body);
        Assert.Contains("static translation prompt", requests[0].Body);
        Assert.Contains("pt-BR", requests[0].Body);
        Assert.Contains("\"prompt_cache_key\":\"batch-cache-v1\"", requests[0].Body);
        Assert.Equal("batch-secret", requests[0].ApiKey);
        Assert.EndsWith("/openai/v1/batches", requests[1].Url);
        Assert.Contains("\"completion_window\":\"24h\"", requests[1].Body);
        Assert.Contains("\"endpoint\":\"/chat/completions\"", requests[1].Body);
    }

    [Fact]
    public async Task GetStatusAsync_ParsesStatusTimestampsCountersAndErrors()
    {
        using var client = new HttpClient(new StubHandler(_ => JsonResponse(new
        {
            id = "batch-456",
            status = "in_progress",
            input_file_id = "file-123",
            output_file_id = "out-1",
            error_file_id = "err-1",
            created_at = 1760000000,
            in_progress_at = 1760000060,
            expires_at = 1760086400,
            request_counts = new { total = 10, completed = 7, failed = 2 },
            errors = new { data = new[] { new { code = "batch_error", message = "A line failed." } } }
        })));
        var status = await CreateProvider(client).GetStatusAsync(ProviderConfig(), "batch-456");

        Assert.Equal("in_progress", status.Status);
        Assert.Equal("out-1", status.OutputFileId);
        Assert.Equal(10, status.TotalRequests);
        Assert.Equal(7, status.CompletedRequests);
        Assert.Equal(2, status.FailedRequests);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000060), status.InProgressAt);
        Assert.Equal("batch_error", Assert.Single(status.Errors).Code);
    }

    [Fact]
    public async Task GetResultsAsync_ParsesJsonlCustomIdsAndResponseBodies()
    {
        var calls = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return calls == 1
                ? JsonResponse(new { id = "batch-1", status = "completed", input_file_id = "file-1", output_file_id = "out-1", created_at = 1760000000, request_counts = new { total = 1, completed = 1, failed = 0 } })
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"custom_id\":\"chunk-a\",\"response\":{\"status_code\":200,\"body\":{\"choices\":[{\"message\":{\"content\":\"translated\"}}]}}}\n") };
        }));

        var line = Assert.Single((await CreateProvider(client).GetResultsAsync(ProviderConfig(), "batch-1")).Lines);

        Assert.Equal("chunk-a", line.CustomId);
        Assert.Equal(200, line.StatusCode);
        Assert.Equal("translated", line.ResponseBody!.Value.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task CancelBatchAsync_PostsToCancelEndpoint()
    {
        HttpRequestMessage? requestSeen = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requestSeen = request;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await CreateProvider(client).CancelBatchAsync(ProviderConfig(), "batch-1");

        Assert.Equal(HttpMethod.Post, requestSeen!.Method);
        Assert.EndsWith("/openai/v1/batches/batch-1/cancel", requestSeen.RequestUri!.ToString());
    }

    [Fact]
    public async Task SubmitBatchAsync_RejectsDuplicateCustomIds()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var item = new BatchTranslationItem("duplicate", Subtitles());

        await Assert.ThrowsAsync<ArgumentException>(() => CreateProvider(client).SubmitBatchAsync(
            ProviderConfig(), ModelConfig(), "prompt", [item, item]));
    }

    private static AzureBatchProvider CreateProvider(HttpClient client) =>
        new(client, new FakeCredentialResolver(), NullLogger<AzureBatchProvider>.Instance);

    private static ProviderConfiguration ProviderConfig() =>
        new(1, "Azure", "AzureOpenAi", "https://resource.openai.azure.com", "ApiKey", "AZURE_OPENAI_KEY", "2025-03-01-preview");

    private static ModelConfiguration ModelConfig() =>
        new(4, "Deployment", "gpt-model", "deployment-x", null, 512, 0.2, 0.9, "MaxTokens", 8000, null, null);

    private static Subtitle[] Subtitles() =>
    [new() { Index = 1, Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2), Text = "Hello" }];

    private static HttpResponseMessage JsonResponse(object value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class FakeCredentialResolver : ICredentialResolver
    {
        public string? Resolve(string? credentialReference) => "batch-secret";
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
