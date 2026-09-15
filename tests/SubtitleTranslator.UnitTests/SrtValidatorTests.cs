using SubtitleTranslator.Domain.ValueObjects;
using SubtitleTranslator.Srt.Validation;

namespace SubtitleTranslator.UnitTests;

public class SrtValidatorTests
{
    private static readonly TimeSpan Start = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan End = TimeSpan.FromSeconds(2);

    [Fact]
    public void Validate_AcceptsSameStructureAndTimestamps()
    {
        var source = new[] { Subtitle(1, "Hello") };
        var translated = new[] { Subtitle(1, "Olá") };

        var result = new SrtValidator().Validate(source, translated);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_ReportsCountIndexTimestampAndEmptyTextErrors()
    {
        var source = new[] { Subtitle(1, "Hello"), Subtitle(2, "World") };
        var translated = new[]
        {
            new Subtitle { Index = 1, Start = Start + TimeSpan.FromSeconds(1), End = End, Text = "Olá" },
            new Subtitle { Index = 1, Start = Start, End = End, Text = "" }
        };

        var result = new SrtValidator().Validate(source, translated);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("timestamps", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("empty", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("duplicated", StringComparison.OrdinalIgnoreCase));
    }

    private static Subtitle Subtitle(int index, string text) =>
        new() { Index = index, Start = Start, End = End, Text = text };
}
