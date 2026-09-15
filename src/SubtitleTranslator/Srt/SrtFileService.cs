using SubtitleTranslator.Domain.ValueObjects;
using SubtitleTranslator.Srt.Parser;
using SubtitleTranslator.Srt.Writer;

namespace SubtitleTranslator.Srt;

public sealed class SrtFileService(SrtParser parser, SrtWriter writer)
{
    public async Task<IReadOnlyList<Subtitle>> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await parser.ParseAsync(stream, cancellationToken);
    }

    public async Task WriteAsync(
        string path,
        IEnumerable<Subtitle> subtitles,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(path);
        await writer.WriteAsync(stream, subtitles, cancellationToken);
    }
}
