namespace SubtitleTranslator.Srt.Parser;

public sealed class SrtParseException(string message, int? subtitleIndex = null)
    : FormatException(subtitleIndex is null ? message : $"Subtitle {subtitleIndex}: {message}")
{
    public int? SubtitleIndex { get; } = subtitleIndex;
}
