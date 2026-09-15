using SubtitleTranslator.Domain.Enums;

namespace SubtitleTranslator.Domain.Entities;

public class ModelDefinition
{
    public int Id { get; set; }
    public int ProviderId { get; set; }
    public Provider? Provider { get; set; }

    public required string Name { get; set; }
    public required string ModelId { get; set; }
    public string? DeploymentName { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }

    public bool SupportsBatch { get; set; }
    public bool SupportsStreaming { get; set; }
    public bool SupportsTranslation { get; set; } = true;

    public int? ContextWindow { get; set; }
    public int? MaxOutputTokens { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }

    public decimal InputCostPer1M { get; set; }
    public decimal OutputCostPer1M { get; set; }
    public decimal? CachedInputCostPer1M { get; set; }

    public ChunkStrategy ChunkStrategy { get; set; } = ChunkStrategy.MaxTokens;
    public int? MaxChunkTokens { get; set; }
    public int? MaxChunkSubtitles { get; set; }

    public string? ConfigurationJson { get; set; }
}
