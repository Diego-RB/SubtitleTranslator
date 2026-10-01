using SubtitleTranslator.Domain.Enums;

namespace SubtitleTranslator.Domain.Entities;

public class TranslationChunk
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public TranslationJob? Job { get; set; }

    public int Sequence { get; set; }
    public int StartSubtitleIndex { get; set; }
    public int EndSubtitleIndex { get; set; }

    public ChunkStatus Status { get; set; } = ChunkStatus.Pending;
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public int Attempt { get; set; }
    public string? ResultJson { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long? LatencyMs { get; set; }
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public ICollection<TranslationAttempt> Attempts { get; set; } = [];
}
