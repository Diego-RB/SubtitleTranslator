using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Application.Interfaces;
using SubtitleTranslator.Domain.ValueObjects;

namespace SubtitleTranslator.Infrastructure.Providers.OpenRouter;

public sealed class OpenRouterProvider(
    HttpClient httpClient,
    ICredentialResolver credentialResolver,
    ILogger<OpenRouterProvider> logger) : ITranslationProvider
{
    private const int DefaultTimeoutSeconds = 120;
    private const int DefaultMaxRetries = 3;
    private const int DefaultRetryDelayMilliseconds = 500;
    private const int DefaultMaxParallelRequests = 4;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> concurrencyLimits = new();

    public string Name => "OpenRouter";

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var options = OpenRouterOptions.Parse(request.Model.ConfigurationJson);
        string? apiKey = null;
        if (!string.Equals(request.Provider.AuthenticationType, "None", StringComparison.OrdinalIgnoreCase))
        {
            apiKey = credentialResolver.Resolve(request.Provider.CredentialId, request.Provider.ProtectedApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("Configured OpenRouter credential is unavailable.");
            }
        }

        var payload = new ChatCompletionRequest(
            request.Model.ModelId,
            [
                new ChatMessage("system", request.Prompt!),
                new ChatMessage("user", BuildUserContent(request))
            ],
            request.Model.Temperature,
            request.Model.TopP,
            request.Model.MaxOutputTokens);

        var endpoint = BuildEndpoint(request.Provider.BaseUrl!);
        var semaphoreKey = $"{request.Provider.Id}:{options.MaxParallelRequests}";
        var semaphore = concurrencyLimits.GetOrAdd(
            semaphoreKey,
            _ => new SemaphoreSlim(options.MaxParallelRequests, options.MaxParallelRequests));

        await semaphore.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
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
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    }
                    AddOptionalHeaders(httpRequest, options);

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
                            "OpenRouter request failed with status {StatusCode} after {Attempts} attempt(s) for model {Model}",
                            (int)response.StatusCode,
                            attempt + 1,
                            request.Model.ModelId);
                        throw new OpenRouterProviderException(
                            $"OpenRouter returned HTTP {(int)response.StatusCode}.",
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

                    throw new OpenRouterProviderException(
                        $"OpenRouter request timed out after {options.TimeoutSeconds} seconds.",
                        isTransient: true);
                }
                catch (HttpRequestException exception) when (attempt < options.MaxRetries)
                {
                    await DelayBeforeRetryAsync(null, attempt, options, cancellationToken);
                    logger.LogDebug(exception, "Retrying transient OpenRouter connection failure");
                }
                catch (HttpRequestException exception)
                {
                    throw new OpenRouterProviderException(
                        "OpenRouter could not be reached.",
                        isTransient: true,
                        innerException: exception);
                }
            }
        }
        finally
        {
            stopwatch.Stop();
            semaphore.Release();
        }
    }

    private static void ValidateRequest(TranslationRequest request)
    {
        if (request.Provider.Id <= 0)
        {
            throw new InvalidOperationException("OpenRouter provider configuration must have a persisted Id.");
        }

        if (string.IsNullOrWhiteSpace(request.Provider.BaseUrl))
        {
            throw new InvalidOperationException("OpenRouter BaseUrl is required.");
        }

        if (!string.Equals(request.Provider.AuthenticationType, "None", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.Provider.CredentialId)
            && string.IsNullOrWhiteSpace(request.Provider.ProtectedApiKey))
        {
            throw new InvalidOperationException("OpenRouter credential is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Model.ModelId))
        {
            throw new InvalidOperationException("OpenRouter model id is required.");
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

    private static string BuildEndpoint(string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/');
        return normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"{normalized}/chat/completions";
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
            glossary = request.GlossaryJson,
            subtitles
        }, JsonOptions);
    }

    private static void AddOptionalHeaders(HttpRequestMessage request, OpenRouterOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.HttpReferer))
        {
            request.Headers.TryAddWithoutValidation("HTTP-Referer", options.HttpReferer);
        }

        if (!string.IsNullOrWhiteSpace(options.XTitle))
        {
            request.Headers.TryAddWithoutValidation("X-Title", options.XTitle);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static async Task DelayBeforeRetryAsync(
        HttpResponseMessage? response,
        int attempt,
        OpenRouterOptions options,
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
        ChatCompletionResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new OpenRouterProviderException("OpenRouter returned invalid JSON.", innerException: exception);
        }

        var content = response?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new OpenRouterProviderException("OpenRouter returned an empty completion.");
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

        OpenRouterSubtitle[]? translated;
        try
        {
            translated = JsonSerializer.Deserialize<OpenRouterSubtitle[]>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new OpenRouterProviderException(
                "OpenRouter response must be a JSON array containing index and text.",
                innerException: exception);
        }

        if (translated is null || translated.Length != source.Count)
        {
            throw new OpenRouterProviderException(
                $"OpenRouter returned {translated?.Length ?? 0} subtitles; expected {source.Count}.");
        }

        var sourceByIndex = source.ToDictionary(subtitle => subtitle.Index);
        var result = new List<Subtitle>(translated.Length);
        foreach (var item in translated)
        {
            if (!sourceByIndex.TryGetValue(item.Index, out var original))
            {
                throw new OpenRouterProviderException($"OpenRouter returned unknown subtitle index {item.Index}.");
            }

            if (string.IsNullOrWhiteSpace(item.Text))
            {
                throw new OpenRouterProviderException($"OpenRouter returned empty text for subtitle {item.Index}.");
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
            throw new OpenRouterProviderException("OpenRouter returned duplicate subtitle indexes.");
        }

        return result.OrderBy(subtitle => subtitle.Index).ToArray();
    }

    private sealed record ChatCompletionRequest(
        string Model,
        IReadOnlyList<ChatMessage> Messages,
        double? Temperature,
        double? TopP,
        int? MaxTokens);

    private sealed record ChatMessage(string Role, string Content);

    private sealed record ChatCompletionResponse(
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

    private sealed record OpenRouterSubtitle(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record OpenRouterOptions(
        int TimeoutSeconds,
        int MaxRetries,
        int RetryDelayMilliseconds,
        int MaxParallelRequests,
        string? HttpReferer,
        string? XTitle)
    {
        public static OpenRouterOptions Parse(string? configurationJson)
        {
            if (string.IsNullOrWhiteSpace(configurationJson))
            {
                return new(DefaultTimeoutSeconds, DefaultMaxRetries, DefaultRetryDelayMilliseconds, DefaultMaxParallelRequests, null, null);
            }

            try
            {
                var configuration = JsonSerializer.Deserialize<Configuration>(configurationJson, ConfigurationJsonOptions)
                    ?? new Configuration();
                return new(
                    Math.Max(1, configuration.TimeoutSeconds ?? DefaultTimeoutSeconds),
                    Math.Max(0, configuration.MaxRetries ?? DefaultMaxRetries),
                    Math.Max(0, configuration.RetryDelayMilliseconds ?? DefaultRetryDelayMilliseconds),
                    Math.Max(1, configuration.MaxParallelRequests ?? DefaultMaxParallelRequests),
                    configuration.HttpReferer,
                    configuration.XTitle);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("OpenRouter ConfigurationJson is invalid.", exception);
            }
        }

        private sealed record Configuration
        {
            public int? TimeoutSeconds { get; init; }
            public int? MaxRetries { get; init; }
            public int? RetryDelayMilliseconds { get; init; }
            public int? MaxParallelRequests { get; init; }
            public string? HttpReferer { get; init; }
            public string? XTitle { get; init; }
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
