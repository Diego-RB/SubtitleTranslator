using SubtitleTranslator.Srt.Parser;

namespace SubtitleTranslator.UnitTests;

public class SrtParserTests
{
    private readonly SrtParser parser = new();

    [Fact]
    public void Parse_ParsesMultilineSubtitleAndCommaMilliseconds()
    {
        var subtitles = parser.Parse("1\r\n00:00:01,250 --> 00:00:03,500\r\nHello\r\nworld\r\n\r\n");

        var subtitle = Assert.Single(subtitles);
        Assert.Equal(1, subtitle.Index);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), subtitle.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3500), subtitle.End);
        Assert.Equal("Hello\nworld", subtitle.Text);
    }

    [Fact]
    public void Parse_RejectsEmptyText()
    {
        var exception = Assert.Throws<SrtParseException>(() =>
            parser.Parse("1\n00:00:01,000 --> 00:00:02,000\n\n"));

        Assert.Contains("text cannot be empty", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsInvalidTiming()
    {
        Assert.Throws<SrtParseException>(() =>
            parser.Parse("1\nnot-a-time --> 00:00:02,000\nText\n"));
    }
}
