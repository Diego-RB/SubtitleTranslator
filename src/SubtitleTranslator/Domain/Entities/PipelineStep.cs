namespace SubtitleTranslator.Domain.Entities;

public class PipelineStep
{
    public int Id { get; set; }
    public int PipelineId { get; set; }
    public Pipeline? Pipeline { get; set; }

    public int ModelDefinitionId { get; set; }
    public ModelDefinition? ModelDefinition { get; set; }

    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
}
