using SubtitleTranslator.Domain.ValueObjects;

namespace SubtitleTranslator.Srt.Chunking;

public sealed record SrtChunk(int Sequence, IReadOnlyList<Subtitle> Subtitles)
{
    public int StartSubtitleIndex => Subtitles[0].Index;
    public int EndSubtitleIndex => Subtitles[^1].Index;
}
