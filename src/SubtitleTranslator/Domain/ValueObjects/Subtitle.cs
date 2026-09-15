namespace SubtitleTranslator.Domain.ValueObjects;

public sealed class Subtitle
{
    public required int Index { get; init; }
    public required TimeSpan Start { get; init; }
    public required TimeSpan End { get; init; }
    public required string Text { get; init; }
}
