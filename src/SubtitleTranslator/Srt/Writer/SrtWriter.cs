using System.Globalization;
using System.Text;
using SubtitleTranslator.Domain.ValueObjects;

namespace SubtitleTranslator.Srt.Writer;

public sealed class SrtWriter
{
    public string Write(IEnumerable<Subtitle> subtitles)
    {
        ArgumentNullException.ThrowIfNull(subtitles);

        var ordered = subtitles.ToList();
        var builder = new StringBuilder();
        for (var i = 0; i < ordered.Count; i++)
        {
            var subtitle = ordered[i];
            builder.Append(subtitle.Index.ToString(CultureInfo.InvariantCulture))
                .Append("\r\n")
                .Append(FormatTimestamp(subtitle.Start))
                .Append(" --> ")
                .Append(FormatTimestamp(subtitle.End))
                .Append("\r\n")
                .Append(subtitle.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal))
                .Append("\r\n\r\n");
        }

        return builder.ToString();
    }

    public async Task WriteAsync(
        Stream stream,
        IEnumerable<Subtitle> subtitles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        await writer.WriteAsync(Write(subtitles).AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static string FormatTimestamp(TimeSpan timestamp) =>
        $"{(int)timestamp.TotalHours:00}:{timestamp.Minutes:00}:{timestamp.Seconds:00},{timestamp.Milliseconds:000}";
}
