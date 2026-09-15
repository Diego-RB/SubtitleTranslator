namespace SubtitleTranslator.Domain.Enums;

public enum JobStatus
{
    Discovered,
    Preparing,
    Queued,
    OpenRouterProcessing,
    AzureBatchSubmitting,
    AzureBatchProcessing,
    AzureBatchFinalizing,
    Retrying,
    LocalProcessing,
    Validating,
    Completed,
    Failed,
    Cancelled
}
