using System.Text.Json;
using SubtitleTranslator.Domain.ValueObjects;

namespace SubtitleTranslator.Application.Contracts;

public sealed record BatchTranslationItem(
    string CustomId,
    IReadOnlyList<Subtitle> Subtitles,
    string SourceLanguage = "en",
    string TargetLanguage = "pt-BR",
    string? GlossaryJson = null,
    bool SupportsPromptCache = false,
    string? PromptCacheKey = null);

public sealed record BatchSubmissionResult(
    string BatchId,
    string InputFileId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    int? TotalRequests,
    int? CompletedRequests,
    int? FailedRequests);

public sealed record BatchStatusResult(
    string BatchId,
    string Status,
    string InputFileId,
    string? OutputFileId,
    string? ErrorFileId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? InProgressAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? LastPolledAt,
    int? TotalRequests,
    int? CompletedRequests,
    int? FailedRequests,
    IReadOnlyList<BatchError> Errors);

public sealed record BatchError(string? Code, string Message, string? CustomId = null);

public sealed record BatchResult(IReadOnlyList<BatchResultLine> Lines);

public sealed record BatchErrorResult(IReadOnlyList<BatchError> Errors);

public sealed record BatchResultLine(
    string CustomId,
    int? StatusCode,
    JsonElement? ResponseBody,
    BatchError? Error);
