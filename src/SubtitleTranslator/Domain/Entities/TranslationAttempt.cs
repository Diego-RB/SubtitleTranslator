namespace SubtitleTranslator.Domain.Entities;

public class TranslationAttempt
{
    public int Id { get; set; }
    public int ChunkId { get; set; }
    public TranslationChunk? Chunk { get; set; }

    public int AttemptNumber { get; set; }
    public required string Provider { get; set; }
    public required string Model { get; set; }

    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long? CachedTokens { get; set; }
    public long? CacheWriteTokens { get; set; }
    public long LatencyMs { get; set; }
    public decimal EstimatedCost { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
