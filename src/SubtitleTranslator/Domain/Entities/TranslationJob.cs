using SubtitleTranslator.Domain.Enums;

namespace SubtitleTranslator.Domain.Entities;

public class TranslationJob
{
    public int Id { get; set; }
    public required string SourcePath { get; set; }
    public string? OutputPath { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Discovered;

    public int? PipelineId { get; set; }
    public Pipeline? Pipeline { get; set; }
    public int? PromptVersionId { get; set; }
    public PromptVersion? PromptVersion { get; set; }

    public int TotalChunks { get; set; }
    public int CompletedChunks { get; set; }
    public int FailedChunks { get; set; }

    public string? CurrentProvider { get; set; }
    public string? CurrentModel { get; set; }
    public string? AzureBatchId { get; set; }
    public string? AzureBatchStatus { get; set; }
    public string? AzureBatchInputFileId { get; set; }
    public string? AzureBatchOutputFileId { get; set; }
    public string? AzureBatchErrorFileId { get; set; }
    public DateTimeOffset? AzureBatchCreatedAt { get; set; }
    public DateTimeOffset? AzureBatchInProgressAt { get; set; }
    public DateTimeOffset? AzureBatchExpiresAt { get; set; }
    public DateTimeOffset? AzureBatchCompletedAt { get; set; }
    public DateTimeOffset? AzureBatchLastPolledAt { get; set; }
    public DateTimeOffset? AzureBatchLastProgressAt { get; set; }
    public int? AzureBatchTotalRequests { get; set; }
    public int? AzureBatchCompletedRequests { get; set; }
    public int? AzureBatchFailedRequests { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }

    public string? LastError { get; set; }
    public int RetryCount { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal EstimatedCost { get; set; }

    public ICollection<TranslationChunk> Chunks { get; set; } = [];
}
