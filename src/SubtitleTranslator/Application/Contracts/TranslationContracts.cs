using SubtitleTranslator.Domain.ValueObjects;

namespace SubtitleTranslator.Application.Contracts;

public sealed record ProviderConfiguration(
    int Id,
    string Name,
    string Type,
    string? BaseUrl,
    string AuthenticationType,
    string? CredentialId,
    string? ApiVersion);

public sealed record ModelConfiguration(
    int Id,
    string Name,
    string ModelId,
    string? DeploymentName,
    int? ContextWindow,
    int? MaxOutputTokens,
    double? Temperature,
    double? TopP,
    string ChunkStrategy,
    int? MaxChunkTokens,
    int? MaxChunkSubtitles,
    string? ConfigurationJson,
    bool SupportsBatch = false);

public sealed record TranslationRequest
{
    public required ProviderConfiguration Provider { get; init; }
    public required ModelConfiguration Model { get; init; }
    public required IReadOnlyList<Subtitle> Subtitles { get; init; }
    public string? Prompt { get; init; }
    public string? GlossaryJson { get; init; }
    public bool SupportsPromptCache { get; init; }
    public string? PromptCacheKey { get; init; }
    public string SourceLanguage { get; init; } = "en";
    public string TargetLanguage { get; init; } = "pt-BR";
}

public sealed record TranslationUsage(
    long InputTokens = 0,
    long OutputTokens = 0,
    long CachedTokens = 0,
    long CacheWriteTokens = 0,
    long LatencyMs = 0);

public sealed record TranslationResult(
    IReadOnlyList<Subtitle> Subtitles,
    TranslationUsage Usage,
    string? ProviderRequestId = null);
