using SubtitleTranslator.Domain.ValueObjects;
using SubtitleTranslator.Srt.Parser;
using SubtitleTranslator.Srt.Writer;

namespace SubtitleTranslator.UnitTests;

public class SrtWriterTests
{
    [Fact]
    public void Write_ProducesParseableSrtWithStableTimestamps()
    {
        var subtitles = new[]
        {
            new Subtitle
            {
                Index = 1,
                Start = TimeSpan.FromHours(123) + TimeSpan.FromMilliseconds(1250),
                End = TimeSpan.FromHours(123) + TimeSpan.FromSeconds(3),
                Text = "Linha 1\nLinha 2"
            }
        };

        var content = new SrtWriter().Write(subtitles);
        var parsed = new SrtParser().Parse(content);

        var result = Assert.Single(parsed);
        Assert.Equal(subtitles[0].Index, result.Index);
        Assert.Equal(subtitles[0].Start, result.Start);
        Assert.Equal(subtitles[0].End, result.End);
        Assert.Equal(subtitles[0].Text, result.Text);
        Assert.Contains("123:00:01,250 --> 123:00:03,000", content);
    }
}
