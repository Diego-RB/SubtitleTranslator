namespace SubtitleTranslator.Domain.Entities;

public class PromptTemplate
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<PromptVersion> Versions { get; set; } = [];
}
