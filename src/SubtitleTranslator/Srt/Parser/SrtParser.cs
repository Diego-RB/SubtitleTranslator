using System.Globalization;
using SubtitleTranslator.Domain.ValueObjects;

namespace SubtitleTranslator.Srt.Parser;

public sealed class SrtParser
{
    public IReadOnlyList<Subtitle> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var subtitles = new List<Subtitle>();
        var position = 0;

        while (position < lines.Length)
        {
            SkipBlankLines(lines, ref position);
            if (position >= lines.Length)
            {
                break;
            }

            var indexLine = lines[position].Trim();
            if (!int.TryParse(indexLine, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index <= 0)
            {
                throw new SrtParseException("Expected a positive subtitle index.");
            }

            position++;
            if (position >= lines.Length || !TryParseTiming(lines[position], out var start, out var end))
            {
                throw new SrtParseException("Expected a timing line after the subtitle index.", index);
            }

            position++;
            var textLines = new List<string>();
            while (position < lines.Length && !string.IsNullOrWhiteSpace(lines[position]))
            {
                textLines.Add(lines[position]);
                position++;
            }

            if (textLines.Count == 0 || string.IsNullOrWhiteSpace(string.Join("\n", textLines)))
            {
                throw new SrtParseException("Subtitle text cannot be empty.", index);
            }

            if (end <= start)
            {
                throw new SrtParseException("End time must be after start time.", index);
            }

            subtitles.Add(new Subtitle
            {
                Index = index,
                Start = start,
                End = end,
                Text = string.Join("\n", textLines)
            });
        }

        if (subtitles.Count == 0)
        {
            throw new SrtParseException("The SRT content does not contain subtitles.");
        }

        return subtitles;
    }

    public async Task<IReadOnlyList<Subtitle>> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return Parse(await reader.ReadToEndAsync(cancellationToken));
    }

    private static void SkipBlankLines(string[] lines, ref int position)
    {
        while (position < lines.Length && string.IsNullOrWhiteSpace(lines[position]))
        {
            position++;
        }
    }

    private static bool TryParseTiming(string line, out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;
        var parts = line.Split("-->", 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && TryParseTimestamp(parts[0], out start)
            && TryParseTimestamp(parts[1].Split(" ", StringSplitOptions.RemoveEmptyEntries)[0], out end);
    }

    private static bool TryParseTimestamp(string value, out TimeSpan timestamp)
    {
        timestamp = default;
        var parts = value.Trim().Replace(",", ".", StringComparison.Ordinal).Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            return false;
        }

        var secondsParts = parts[2].Split('.', 2);
        if (!int.TryParse(secondsParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || minutes is < 0 or > 59
            || seconds is < 0 or > 59)
        {
            return false;
        }

        var milliseconds = 0;
        if (secondsParts.Length == 2
            && (secondsParts[1].Length is < 1 or > 3
                || !int.TryParse(secondsParts[1].PadRight(3, '0'), NumberStyles.None, CultureInfo.InvariantCulture, out milliseconds)))
        {
            return false;
        }

        try
        {
            timestamp = new TimeSpan(hours, minutes, seconds).Add(TimeSpan.FromMilliseconds(milliseconds));
            return hours >= 0;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
