namespace SubtitleTranslator.Domain.Entities;

public class PromptVersion
{
    public int Id { get; set; }
    public int PromptTemplateId { get; set; }
    public PromptTemplate? PromptTemplate { get; set; }

    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public required string StaticContent { get; set; }
    public string? GlossaryJson { get; set; }
    public bool Enabled { get; set; } = true;
    public bool SupportsPromptCache { get; set; }
    public string? PromptCacheKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
