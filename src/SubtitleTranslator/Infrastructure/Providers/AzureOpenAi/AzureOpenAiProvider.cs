using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Application.Interfaces;
using SubtitleTranslator.Domain.ValueObjects;

namespace SubtitleTranslator.Infrastructure.Providers.AzureOpenAi;

public sealed class AzureOpenAiProvider(
    HttpClient httpClient,
    ICredentialResolver credentialResolver,
    ILogger<AzureOpenAiProvider> logger) : ITranslationProvider
{
    private const int DefaultTimeoutSeconds = 120;
    private const int DefaultMaxRetries = 3;
    private const int DefaultRetryDelayMilliseconds = 500;

    public string Name => "AzureOpenAi";

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var isV1Endpoint = IsAzureOpenAiV1Endpoint(request.Provider.BaseUrl);
        ValidateRequest(request, isV1Endpoint);

        var options = AzureOptions.Parse(request.Model.ConfigurationJson);
        var credential = credentialResolver.Resolve(request.Provider.CredentialId, request.Provider.ProtectedApiKey);
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new InvalidOperationException("Configured Azure OpenAI credential is unavailable.");
        }

        var endpoint = BuildEndpoint(
            request.Provider.BaseUrl!,
            request.Model.DeploymentName!,
            request.Provider.ApiVersion,
            isV1Endpoint);
        var promptCacheKey = !isV1Endpoint
            && request.SupportsPromptCache
            && !string.IsNullOrWhiteSpace(request.PromptCacheKey)
            ? request.PromptCacheKey
            : null;
        var payload = new ChatCompletionRequest(
            isV1Endpoint ? request.Model.DeploymentName : null,
            [
                new ChatMessage("system", BuildSystemPrompt(request)),
                new ChatMessage("user", BuildUserContent(request))
            ],
            request.Model.Temperature,
            request.Model.TopP,
            isV1Endpoint ? null : request.Model.MaxOutputTokens,
            isV1Endpoint ? request.Model.MaxOutputTokens : null,
            promptCacheKey);

        var stopwatch = Stopwatch.StartNew();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(payload, options: JsonOptions)
                };
                httpRequest.Headers.TryAddWithoutValidation("api-key", credential);
                if (isV1Endpoint)
                {
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                }

                using var response = await httpClient.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var transient = IsTransient(response.StatusCode);
                    if (transient && attempt < options.MaxRetries)
                    {
                        await DelayBeforeRetryAsync(response, attempt, options, cancellationToken);
                        continue;
                    }

                    logger.LogWarning(
                        "Azure OpenAI request failed with status {StatusCode} after {Attempts} attempt(s) for deployment {Deployment}",
                        (int)response.StatusCode,
                        attempt + 1,
                        request.Model.DeploymentName);
                    var error = ParseErrorResponse(responseBody, credential);
                    var detail = string.IsNullOrWhiteSpace(error.Message)
                        ? string.Empty
                        : $" ({error.Code ?? "Azure error"}: {error.Message})";
                    throw new AzureOpenAiProviderException(
                        $"Azure OpenAI returned HTTP {(int)response.StatusCode}{detail}",
                        (int)response.StatusCode,
                        transient);
                }

                return ParseResponse(responseBody, request.Subtitles, stopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < options.MaxRetries)
                {
                    await DelayBeforeRetryAsync(null, attempt, options, cancellationToken);
                    continue;
                }

                throw new AzureOpenAiProviderException(
                    $"Azure OpenAI request timed out after {options.TimeoutSeconds} seconds.",
                    isTransient: true);
            }
            catch (HttpRequestException exception) when (attempt < options.MaxRetries)
            {
                await DelayBeforeRetryAsync(null, attempt, options, cancellationToken);
                logger.LogDebug(exception, "Retrying transient Azure OpenAI connection failure");
            }
            catch (HttpRequestException exception)
            {
                throw new AzureOpenAiProviderException(
                    "Azure OpenAI could not be reached.",
                    isTransient: true,
                    innerException: exception);
            }
            finally
            {
                stopwatch.Stop();
            }
        }
    }

    private static void ValidateRequest(TranslationRequest request, bool isV1Endpoint)
    {
        if (request.Provider.Id <= 0)
        {
            throw new InvalidOperationException("Azure OpenAI provider configuration must have a persisted Id.");
        }

        if (string.IsNullOrWhiteSpace(request.Provider.BaseUrl))
        {
            throw new InvalidOperationException("Azure OpenAI BaseUrl is required.");
        }

        if (!isV1Endpoint && string.IsNullOrWhiteSpace(request.Provider.ApiVersion))
        {
            throw new InvalidOperationException("Azure OpenAI ApiVersion is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Provider.CredentialId)
            && string.IsNullOrWhiteSpace(request.Provider.ProtectedApiKey))
        {
            throw new InvalidOperationException("Azure OpenAI credential is required for API-key authentication.");
        }

        if (!string.Equals(request.Provider.AuthenticationType, "ApiKey", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.Provider.AuthenticationType, "AzureKeyCredential", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "Phase 6 supports Azure OpenAI API-key authentication. Managed Identity is not implemented yet.");
        }

        if (string.IsNullOrWhiteSpace(request.Model.DeploymentName))
        {
            throw new InvalidOperationException("Azure OpenAI DeploymentName is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new InvalidOperationException("A configured translation prompt is required.");
        }

        if (request.Subtitles.Count == 0)
        {
            throw new InvalidOperationException("At least one subtitle is required.");
        }
    }

    private static bool IsAzureOpenAiV1Endpoint(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        var normalized = baseUrl.TrimEnd('/');
        if (normalized.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^"/responses".Length];
        }

        return normalized.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/openai/v1/chat/completions", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildEndpoint(string baseUrl, string deployment, string? apiVersion, bool isV1Endpoint)
    {
        var normalized = baseUrl.TrimEnd('/');
        if (isV1Endpoint)
        {
            if (normalized.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^"/responses".Length];
            }

            return normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : $"{normalized}/chat/completions";
        }

        if (normalized.Contains("/openai/deployments/", StringComparison.OrdinalIgnoreCase)
            && normalized.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return AddApiVersion(normalized, apiVersion!);
        }

        return AddApiVersion(
            $"{normalized}/openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions",
            apiVersion!);
    }

    private static string AddApiVersion(string endpoint, string apiVersion)
    {
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{endpoint}{separator}api-version={Uri.EscapeDataString(apiVersion)}";
    }

    private static string BuildSystemPrompt(TranslationRequest request)
    {
        if (!request.SupportsPromptCache || string.IsNullOrWhiteSpace(request.GlossaryJson))
        {
            return request.Prompt!;
        }

        return $"{request.Prompt!.Trim()}\n\nGlossary:\n{request.GlossaryJson.Trim()}";
    }

    private static string BuildUserContent(TranslationRequest request)
    {
        var subtitles = request.Subtitles.Select(subtitle => new
        {
            index = subtitle.Index,
            text = subtitle.Text
        });
        return JsonSerializer.Serialize(new
        {
            sourceLanguage = request.SourceLanguage,
            targetLanguage = request.TargetLanguage,
            glossary = request.SupportsPromptCache ? null : request.GlossaryJson,
            subtitles
        }, JsonOptions);
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static (string? Code, string? Message) ParseErrorResponse(string responseBody, string credential)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var code = error.TryGetProperty("code", out var codeElement)
                && codeElement.ValueKind == JsonValueKind.String
                    ? codeElement.GetString()
                    : null;
            var message = error.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()
                    : null;
            if (!string.IsNullOrWhiteSpace(message))
            {
                message = message.Replace(credential, "[REDACTED]", StringComparison.Ordinal);
                message = message.Length > 512 ? $"{message[..512]}…" : message;
            }

            return (code, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static async Task DelayBeforeRetryAsync(
        HttpResponseMessage? response,
        int attempt,
        AzureOptions options,
        CancellationToken cancellationToken)
    {
        var retryAfter = response?.Headers.RetryAfter?.Delta;
        var delay = retryAfter ?? TimeSpan.FromMilliseconds(
            options.RetryDelayMilliseconds * Math.Pow(2, Math.Min(attempt, 6)));
        await Task.Delay(delay, cancellationToken);
    }

    private static TranslationResult ParseResponse(
        string responseBody,
        IReadOnlyList<Subtitle> source,
        long latencyMs)
    {
        AzureChatCompletionResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<AzureChatCompletionResponse>(responseBody, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new AzureOpenAiProviderException("Azure OpenAI returned invalid JSON.", innerException: exception);
        }

        var content = response?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new AzureOpenAiProviderException("Azure OpenAI returned an empty completion.");
        }

        var translated = ParseTranslatedSubtitles(content, source);
        var usage = new TranslationUsage(
            InputTokens: response?.Usage?.PromptTokens ?? 0,
            OutputTokens: response?.Usage?.CompletionTokens ?? 0,
            CachedTokens: response?.Usage?.PromptTokensDetails?.CachedTokens ?? 0,
            CacheWriteTokens: response?.Usage?.PromptTokensDetails?.CacheWriteTokens ?? 0,
            LatencyMs: latencyMs);
        return new TranslationResult(translated, usage, response?.Id);
    }

    private static IReadOnlyList<Subtitle> ParseTranslatedSubtitles(
        string response,
        IReadOnlyList<Subtitle> source)
    {
        var json = response.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            json = json.Trim('`', ' ', '\r', '\n');
            if (json.StartsWith("json\n", StringComparison.OrdinalIgnoreCase))
            {
                json = json[5..];
            }
        }

        AzureSubtitle[]? translated;
        try
        {
            translated = JsonSerializer.Deserialize<AzureSubtitle[]>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new AzureOpenAiProviderException(
                "Azure OpenAI response must be a JSON array containing index and text.",
                innerException: exception);
        }

        if (translated is null || translated.Length != source.Count)
        {
            throw new AzureOpenAiProviderException(
                $"Azure OpenAI returned {translated?.Length ?? 0} subtitles; expected {source.Count}.");
        }

        var sourceByIndex = source.ToDictionary(subtitle => subtitle.Index);
        var result = new List<Subtitle>(translated.Length);
        foreach (var item in translated)
        {
            if (!sourceByIndex.TryGetValue(item.Index, out var original))
            {
                throw new AzureOpenAiProviderException($"Azure OpenAI returned unknown subtitle index {item.Index}.");
            }

            if (string.IsNullOrWhiteSpace(item.Text))
            {
                throw new AzureOpenAiProviderException($"Azure OpenAI returned empty text for subtitle {item.Index}.");
            }

            result.Add(new Subtitle
            {
                Index = original.Index,
                Start = original.Start,
                End = original.End,
                Text = item.Text
            });
        }

        if (result.Select(subtitle => subtitle.Index).Distinct().Count() != source.Count)
        {
            throw new AzureOpenAiProviderException("Azure OpenAI returned duplicate subtitle indexes.");
        }

        return result.OrderBy(subtitle => subtitle.Index).ToArray();
    }

    private sealed record ChatCompletionRequest(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Model,
        IReadOnlyList<ChatMessage> Messages,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Temperature,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? TopP,
        [property: JsonPropertyName("max_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? MaxTokens,
        [property: JsonPropertyName("max_completion_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? MaxCompletionTokens,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PromptCacheKey = null);

    private sealed record ChatMessage(string Role, string Content);

    private sealed record AzureChatCompletionResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("choices")] IReadOnlyList<Choice>? Choices,
        [property: JsonPropertyName("usage")] Usage? Usage);

    private sealed record Choice(
        [property: JsonPropertyName("message")] ChatMessage? Message);

    private sealed record Usage(
        [property: JsonPropertyName("prompt_tokens")] long PromptTokens,
        [property: JsonPropertyName("completion_tokens")] long CompletionTokens,
        [property: JsonPropertyName("prompt_tokens_details")] PromptTokensDetails? PromptTokensDetails);

    private sealed record PromptTokensDetails(
        [property: JsonPropertyName("cached_tokens")] long CachedTokens,
        [property: JsonPropertyName("cache_write_tokens")] long CacheWriteTokens = 0);

    private sealed record AzureSubtitle(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record AzureOptions(int TimeoutSeconds, int MaxRetries, int RetryDelayMilliseconds)
    {
        public static AzureOptions Parse(string? configurationJson)
        {
            if (string.IsNullOrWhiteSpace(configurationJson))
            {
                return new(DefaultTimeoutSeconds, DefaultMaxRetries, DefaultRetryDelayMilliseconds);
            }

            try
            {
                var configuration = JsonSerializer.Deserialize<Configuration>(configurationJson, ConfigurationJsonOptions)
                    ?? new Configuration();
                return new(
                    Math.Max(1, configuration.TimeoutSeconds ?? DefaultTimeoutSeconds),
                    Math.Max(0, configuration.MaxRetries ?? DefaultMaxRetries),
                    Math.Max(0, configuration.RetryDelayMilliseconds ?? DefaultRetryDelayMilliseconds));
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("Azure OpenAI ConfigurationJson is invalid.", exception);
            }
        }

        private sealed record Configuration
        {
            public int? TimeoutSeconds { get; init; }
            public int? MaxRetries { get; init; }
            public int? RetryDelayMilliseconds { get; init; }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static readonly JsonSerializerOptions ConfigurationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
