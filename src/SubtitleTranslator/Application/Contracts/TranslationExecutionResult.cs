namespace SubtitleTranslator.Application.Contracts;

public sealed record TranslationExecutionResult(
    int JobId,
    bool Succeeded,
    string? OutputPath,
    int CompletedSubtitles,
    int FailedSubtitles,
    int Attempts,
    decimal EstimatedCost,
    IReadOnlyList<string> Errors);
