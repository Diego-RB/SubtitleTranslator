namespace SubtitleTranslator.Domain.Enums;

public enum JobStatus
{
    Discovered,
    Preparing,
    Queued,
    OpenRouterProcessing,
    AzureOpenAiProcessing,
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
