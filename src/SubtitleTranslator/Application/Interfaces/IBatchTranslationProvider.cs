using SubtitleTranslator.Application.Contracts;

namespace SubtitleTranslator.Application.Interfaces;

public interface IBatchTranslationProvider
{
    Task<BatchSubmissionResult> SubmitBatchAsync(
        ProviderConfiguration provider,
        ModelConfiguration model,
        string prompt,
        IReadOnlyList<BatchTranslationItem> items,
        CancellationToken cancellationToken = default);

    Task<BatchStatusResult> GetStatusAsync(
        ProviderConfiguration provider,
        string batchId,
        CancellationToken cancellationToken = default);

    Task<BatchResult> GetResultsAsync(
        ProviderConfiguration provider,
        string batchId,
        CancellationToken cancellationToken = default);

    Task<BatchErrorResult> GetErrorsAsync(
        ProviderConfiguration provider,
        string batchId,
        CancellationToken cancellationToken = default);

    Task CancelBatchAsync(
        ProviderConfiguration provider,
        string batchId,
        CancellationToken cancellationToken = default);
}
