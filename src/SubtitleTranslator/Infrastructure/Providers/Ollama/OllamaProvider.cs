using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubtitleTranslator.Application.Contracts;
using SubtitleTranslator.Application.Interfaces;
using SubtitleTranslator.Domain.ValueObjects;

namespace SubtitleTranslator.Infrastructure.Providers.Ollama;

public sealed class OllamaProvider(
    HttpClient httpClient,
    ILogger<OllamaProvider> logger) : ITranslationProvider
{
    private const int DefaultMaxParallelRequests = 1;
    private const int DefaultTimeoutSeconds = 120;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> concurrencyLimits = new();

    public string Name => "Ollama";

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var options = OllamaModelOptions.Parse(request.Model.ConfigurationJson);
        var semaphoreKey = request.Provider.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var semaphore = concurrencyLimits.GetOrAdd(
            semaphoreKey,
            _ => new SemaphoreSlim(Math.Max(1, options.MaxParallelRequests), Math.Max(1, options.MaxParallelRequests)));

        await semaphore.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

            var endpoint = BuildEndpoint(request.Provider.BaseUrl!);
            var payload = new OllamaGenerateRequest(
                request.Model.ModelId,
                request.Prompt!,
                Stream: false,
                new OllamaGenerateOptions(
                    options.Temperature ?? request.Model.Temperature,
                    request.Model.TopP,
                    options.NumPredict ?? request.Model.MaxOutputTokens));

            using var response = await httpClient.PostAsJsonAsync(endpoint, payload, JsonOptions, timeout.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Ollama request failed with status {StatusCode} for model {Model}",
                    (int)response.StatusCode,
                    request.Model.ModelId);
                throw new OllamaProviderException(
                    $"Ollama returned HTTP {(int)response.StatusCode}.",
                    (int)response.StatusCode);
            }

            OllamaGenerateResponse? ollamaResponse;
            try
            {
                ollamaResponse = JsonSerializer.Deserialize<OllamaGenerateResponse>(responseBody, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new OllamaProviderException("Ollama returned invalid JSON.", innerException: exception);
            }

            if (ollamaResponse is null || string.IsNullOrWhiteSpace(ollamaResponse.Response))
            {
                throw new OllamaProviderException("Ollama returned an empty response.");
            }

            var translated = ParseTranslatedSubtitles(ollamaResponse.Response, request.Subtitles);
            var usage = new TranslationUsage(
                InputTokens: ollamaResponse.PromptEvalCount ?? 0,
                OutputTokens: ollamaResponse.EvalCount ?? 0,
                LatencyMs: stopwatch.ElapsedMilliseconds);

            return new TranslationResult(translated, usage);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OllamaProviderException(
                $"Ollama request timed out after {options.TimeoutSeconds} seconds.");
        }
        catch (HttpRequestException exception)
        {
            throw new OllamaProviderException("Ollama could not be reached.", innerException: exception);
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
            throw new InvalidOperationException("Ollama provider configuration must have a persisted Id.");
        }

        if (string.IsNullOrWhiteSpace(request.Provider.BaseUrl))
        {
            throw new InvalidOperationException("Ollama BaseUrl is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Model.ModelId))
        {
            throw new InvalidOperationException("Ollama model id is required.");
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
        return normalized.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"{normalized}/api/generate";
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

        OllamaSubtitle[]? translated;
        try
        {
            translated = JsonSerializer.Deserialize<OllamaSubtitle[]>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new OllamaProviderException(
                "Ollama response must be a JSON array containing index and text.",
                innerException: exception);
        }

        if (translated is null || translated.Length != source.Count)
        {
            throw new OllamaProviderException(
                $"Ollama returned {translated?.Length ?? 0} subtitles; expected {source.Count}.");
        }

        var sourceByIndex = source.ToDictionary(subtitle => subtitle.Index);
        var result = new List<Subtitle>(translated.Length);
        foreach (var item in translated)
        {
            if (!sourceByIndex.TryGetValue(item.Index, out var original))
            {
                throw new OllamaProviderException($"Ollama returned unknown subtitle index {item.Index}.");
            }

            if (string.IsNullOrWhiteSpace(item.Text))
            {
                throw new OllamaProviderException($"Ollama returned empty text for subtitle {item.Index}.");
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
            throw new OllamaProviderException("Ollama returned duplicate subtitle indexes.");
        }

        return result.OrderBy(subtitle => subtitle.Index).ToArray();
    }

    private sealed record OllamaGenerateRequest(
        string Model,
        string Prompt,
        bool Stream,
        OllamaGenerateOptions Options);

    private sealed record OllamaGenerateOptions(
        [property: JsonPropertyName("temperature")] double? Temperature,
        [property: JsonPropertyName("top_p")] double? TopP,
        [property: JsonPropertyName("num_predict")] int? NumPredict);

    private sealed record OllamaGenerateResponse(
        [property: JsonPropertyName("response")] string? Response,
        [property: JsonPropertyName("prompt_eval_count")] long? PromptEvalCount,
        [property: JsonPropertyName("eval_count")] long? EvalCount);

    private sealed record OllamaSubtitle(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record OllamaModelOptions(
        int TimeoutSeconds,
        int MaxParallelRequests,
        int? NumPredict,
        double? Temperature)
    {
        public static OllamaModelOptions Parse(string? configurationJson)
        {
            if (string.IsNullOrWhiteSpace(configurationJson))
            {
                return new(DefaultTimeoutSeconds, DefaultMaxParallelRequests, null, null);
            }

            try
            {
                var configuration = JsonSerializer.Deserialize<Configuration>(configurationJson, ConfigurationJsonOptions)
                    ?? new Configuration();
                return new(
                    Math.Max(1, configuration.TimeoutSeconds ?? DefaultTimeoutSeconds),
                    Math.Max(1, configuration.MaxParallelRequests ?? DefaultMaxParallelRequests),
                    configuration.NumPredict,
                    configuration.Temperature);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("Ollama ConfigurationJson is invalid.", exception);
            }
        }

        private sealed record Configuration
        {
            public int? TimeoutSeconds { get; init; }
            public int? MaxParallelRequests { get; init; }
            public int? NumPredict { get; init; }
            public double? Temperature { get; init; }
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
