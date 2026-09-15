namespace SubtitleTranslator.Domain.Enums;

public enum ChunkStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    RetryPending,
    Cancelled
}
