using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Application.Interfaces;

namespace SubtitleTranslator.Infrastructure.Providers.AzureBatch;

public sealed class AzureBatchProvider(
    HttpClient httpClient,
    ICredentialResolver credentialResolver,
    ILogger<AzureBatchProvider> logger) : IBatchTranslationProvider
{
    private const string BatchEndpoint = "/chat/completions";
    private const string CompletionWindow = "24h";

    public string Name => "AzureOpenAi";

    public async Task<BatchSubmissionResult> SubmitBatchAsync(
        ProviderConfiguration provider,
        ModelConfiguration model,
        string prompt,
        IReadOnlyList<BatchTranslationItem> items,
        CancellationToken cancellationToken = default)
    {
        Validate(provider, model);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("A configured prompt is required.", nameof(prompt));
        }

        if (items.Count == 0)
        {
            throw new ArgumentException("At least one batch item is required.", nameof(items));
        }

        if (items.Select(item => item.CustomId).Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            throw new ArgumentException("Batch item custom IDs must be unique.", nameof(items));
        }

        var jsonl = BuildJsonl(model, prompt, items);
        var baseAddress = GetApiBase(provider.BaseUrl!);
        var inputFileId = await UploadInputFileAsync(provider, baseAddress, jsonl, cancellationToken);
        var batch = await CreateBatchAsync(provider, baseAddress, inputFileId, cancellationToken);
        logger.LogInformation(
            "Azure Batch submitted {BatchId} with {RequestCount} requests for deployment {Deployment}",
            batch.BatchId,
            items.Count,
            model.DeploymentName);
        return batch;
    }

    public async Task<BatchStatusResult> GetStatusAsync(
        ProviderConfiguration provider,
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ValidateProvider(provider);
        var endpoint = $"{GetApiBase(provider.BaseUrl!)}/batches/{Uri.EscapeDataString(Required(batchId, nameof(batchId)))}";
        using var request = CreateRequest(HttpMethod.Get, endpoint, provider);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var content = await ReadSuccessfulContentAsync(response, cancellationToken);
        return ParseStatus(content, DateTimeOffset.UtcNow);
    }

    public async Task<BatchResult> GetResultsAsync(
        ProviderConfiguration provider,
        string batchId,
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(provider, batchId, cancellationToken);
        if (string.IsNullOrWhiteSpace(status.OutputFileId))
        {
            return new BatchResult([]);
        }

        var jsonl = await DownloadFileAsync(provider, status.OutputFileId, cancellationToken);
        return new BatchResult(ParseResultLines(jsonl));
    }

    public async Task<BatchErrorResult> GetErrorsAsync(
        ProviderConfiguration provider,
        string batchId,
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(provider, batchId, cancellationToken);
        var errors = status.Errors.ToList();
        if (!string.IsNullOrWhiteSpace(status.ErrorFileId))
        {
            var jsonl = await DownloadFileAsync(provider, status.ErrorFileId, cancellationToken);
            errors.AddRange(ParseErrorLines(jsonl));
        }

        return new BatchErrorResult(errors);
    }

    public async Task CancelBatchAsync(
        ProviderConfiguration provider,
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ValidateProvider(provider);
        var endpoint = $"{GetApiBase(provider.BaseUrl!)}/batches/{Uri.EscapeDataString(Required(batchId, nameof(batchId)))}/cancel";
        using var request = CreateRequest(HttpMethod.Post, endpoint, provider);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<string> UploadInputFileAsync(
        ProviderConfiguration provider,
        string apiBase,
        string jsonl,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, $"{apiBase}/files", provider);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("batch"), "purpose");
        var fileContent = new ByteArrayContent(new UTF8Encoding(false).GetBytes(jsonl));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(fileContent, "file", "subtitle-translator.jsonl");
        request.Content = form;

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var content = await ReadSuccessfulContentAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(content);
        var fileId = ReadString(document.RootElement, "id");
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new AzureBatchProviderException("Azure Batch file upload response did not include a file ID.");
        }

        return fileId;
    }

    private async Task<BatchSubmissionResult> CreateBatchAsync(
        ProviderConfiguration provider,
        string apiBase,
        string inputFileId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, $"{apiBase}/batches", provider);
        request.Content = JsonContent.Create(new CreateBatchRequest(inputFileId, BatchEndpoint, CompletionWindow));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var content = await ReadSuccessfulContentAsync(response, cancellationToken);
        var status = ParseStatus(content, DateTimeOffset.UtcNow);
        if (string.IsNullOrWhiteSpace(status.BatchId))
        {
            throw new AzureBatchProviderException("Azure Batch create response did not include a batch ID.");
        }

        return new BatchSubmissionResult(
            status.BatchId,
            inputFileId,
            status.Status,
            status.CreatedAt,
            status.ExpiresAt,
            status.TotalRequests,
            status.CompletedRequests,
            status.FailedRequests);
    }

    private async Task<string> DownloadFileAsync(
        ProviderConfiguration provider,
        string fileId,
        CancellationToken cancellationToken)
    {
        ValidateProvider(provider);
        var endpoint = $"{GetApiBase(provider.BaseUrl!)}/files/{Uri.EscapeDataString(fileId)}/content";
        using var request = CreateRequest(HttpMethod.Get, endpoint, provider);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessfulContentAsync(response, cancellationToken);
    }

    private async Task<string> ReadSuccessfulContentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Azure Batch API returned HTTP {StatusCode}", (int)response.StatusCode);
            throw new AzureBatchProviderException(
                $"Azure Batch API returned HTTP {(int)response.StatusCode}.",
                (int)response.StatusCode);
        }

        return content;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Azure Batch API returned HTTP {StatusCode}", (int)response.StatusCode);
            throw new AzureBatchProviderException(
                $"Azure Batch API returned HTTP {(int)response.StatusCode}.",
                (int)response.StatusCode);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint, ProviderConfiguration provider)
    {
        var credential = credentialResolver.Resolve(provider.CredentialId);
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new InvalidOperationException(
                $"Credential '{provider.CredentialId}' was not found in the environment.");
        }

        if (!string.Equals(provider.AuthenticationType, "ApiKey", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Azure Batch currently supports API-key authentication only.");
        }

        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.TryAddWithoutValidation("api-key", credential);
        return request;
    }

    private static string BuildJsonl(
        ModelConfiguration model,
        string prompt,
        IReadOnlyList<BatchTranslationItem> items)
    {
        var deployment = Required(model.DeploymentName, nameof(model.DeploymentName));
        var builder = new StringBuilder();
        foreach (var item in items)
        {
            var systemPrompt = item.SupportsPromptCache && !string.IsNullOrWhiteSpace(item.GlossaryJson)
                ? $"{prompt.Trim()}\n\nGlossary:\n{item.GlossaryJson.Trim()}"
                : prompt;
            var promptCacheKey = item.SupportsPromptCache && !string.IsNullOrWhiteSpace(item.PromptCacheKey)
                ? item.PromptCacheKey
                : null;
            var body = new BatchRequestBody(
                deployment,
                [
                    new BatchChatMessage("system", systemPrompt),
                    new BatchChatMessage(
                        "user",
                        JsonSerializer.Serialize(new
                        {
                            sourceLanguage = item.SourceLanguage,
                            targetLanguage = item.TargetLanguage,
                            glossary = item.SupportsPromptCache ? null : item.GlossaryJson,
                            subtitles = item.Subtitles.Select(subtitle => new
                            {
                                index = subtitle.Index,
                                text = subtitle.Text
                            })
                        }, JsonOptions))
                ],
                model.Temperature,
                model.TopP,
                model.MaxOutputTokens,
                promptCacheKey);
            var line = JsonSerializer.Serialize(new
            {
                custom_id = item.CustomId,
                method = "POST",
                url = "/chat/completions",
                body
            }, JsonOptions);
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    private sealed record BatchChatMessage(string Role, string Content);

    private sealed record BatchRequestBody(
        string Model,
        IReadOnlyList<BatchChatMessage> Messages,
        double? Temperature,
        double? TopP,
        int? MaxTokens,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PromptCacheKey = null);

    private static BatchStatusResult ParseStatus(string content, DateTimeOffset polledAt)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var counts = GetProperty(root, "request_counts");
        return new BatchStatusResult(
            ReadString(root, "id") ?? string.Empty,
            ReadString(root, "status") ?? "unknown",
            ReadString(root, "input_file_id") ?? string.Empty,
            ReadString(root, "output_file_id"),
            ReadString(root, "error_file_id"),
            ReadTimestamp(root, "created_at") ?? polledAt,
            ReadTimestamp(root, "in_progress_at"),
            ReadTimestamp(root, "expires_at"),
            ReadTimestamp(root, "completed_at"),
            polledAt,
            ReadInt(counts, "total"),
            ReadInt(counts, "completed"),
            ReadInt(counts, "failed"),
            ParseBatchErrors(GetProperty(root, "errors")));
    }

    private static IReadOnlyList<BatchResultLine> ParseResultLines(string jsonl)
    {
        var results = new List<BatchResultLine>();
        foreach (var line in ReadJsonLines(jsonl))
        {
            var customId = ReadString(line, "custom_id") ?? string.Empty;
            var response = GetProperty(line, "response");
            var responseBody = GetProperty(response, "body");
            var statusCode = ReadInt(response, "status_code");
            var errorElement = GetProperty(line, "error");
            var error = errorElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? null
                : ParseError(errorElement, customId);
            results.Add(new BatchResultLine(
                customId,
                statusCode,
                responseBody.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : responseBody.Clone(),
                error));
        }

        return results;
    }

    private static IReadOnlyList<BatchError> ParseErrorLines(string jsonl)
    {
        var errors = new List<BatchError>();
        foreach (var line in ReadJsonLines(jsonl))
        {
            var customId = ReadString(line, "custom_id");
            var error = GetProperty(line, "error");
            errors.Add(error.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? new BatchError(null, "Batch request failed.", customId)
                : ParseError(error, customId));
        }

        return errors;
    }

    private static IEnumerable<JsonElement> ReadJsonLines(string content)
    {
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                using var document = JsonDocument.Parse(line);
                yield return document.RootElement.Clone();
            }
        }
    }

    private static IReadOnlyList<BatchError> ParseBatchErrors(JsonElement errors)
    {
        var data = GetProperty(errors, "data");
        if (data.ValueKind != JsonValueKind.Array)
        {
            data = GetProperty(errors, "value");
        }

        return data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(error => ParseError(error, null)).ToArray()
            : [];
    }

    private static BatchError ParseError(JsonElement error, string? customId) =>
        new(ReadString(error, "code"), ReadString(error, "message") ?? "Batch request failed.", customId);

    private static string GetApiBase(string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/');
        var suffix = "/openai/v1";
        var position = normalized.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
        if (position >= 0)
        {
            normalized = normalized[..(position + suffix.Length)];
        }
        else
        {
            normalized += suffix;
        }

        return normalized;
    }

    private static void Validate(ProviderConfiguration provider, ModelConfiguration model)
    {
        ValidateProvider(provider);
        Required(model.DeploymentName, nameof(model.DeploymentName));
    }

    private static void ValidateProvider(ProviderConfiguration provider)
    {
        if (!string.Equals(provider.Type, "AzureOpenAi", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Provider type must be AzureOpenAi.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl)
            || !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Azure OpenAI BaseUrl must be an absolute HTTPS URL.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(provider.CredentialId))
        {
            throw new ArgumentException("Azure OpenAI CredentialId is required.", nameof(provider));
        }
    }

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} is required.", parameterName)
            : value;

    private static JsonElement GetProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : default;

    private static string? ReadString(JsonElement element, string name)
    {
        var value = GetProperty(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        var value = GetProperty(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string name)
    {
        var value = GetProperty(element, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                return timestamp;
            }

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }

        return null;
    }

    private sealed record CreateBatchRequest(
        [property: JsonPropertyName("input_file_id")] string InputFileId,
        [property: JsonPropertyName("endpoint")] string Endpoint,
        [property: JsonPropertyName("completion_window")] string CompletionWindow);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}
