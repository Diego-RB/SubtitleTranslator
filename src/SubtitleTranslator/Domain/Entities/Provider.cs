using SubtitleTranslator.Domain.Enums;

namespace SubtitleTranslator.Domain.Entities;

public class Provider
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public ProviderType Type { get; set; }
    public string? BaseUrl { get; set; }
    public AuthenticationType AuthenticationType { get; set; }
    public string? CredentialId { get; set; }
    public string? ApiVersion { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ModelDefinition> Models { get; set; } = [];
}
