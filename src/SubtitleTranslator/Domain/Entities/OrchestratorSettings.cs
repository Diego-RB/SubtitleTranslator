namespace SubtitleTranslator.Domain.Entities;

public class OrchestratorSettings
{
    public int Id { get; set; } = 1;
    public int? DefaultPipelineId { get; set; }
    public Pipeline? DefaultPipeline { get; set; }
    public int? DefaultPromptVersionId { get; set; }
    public PromptVersion? DefaultPromptVersion { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
