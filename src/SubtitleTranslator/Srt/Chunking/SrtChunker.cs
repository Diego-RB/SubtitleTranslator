using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Domain.ValueObjects;

namespace SubtitleTranslator.Srt.Chunking;

public sealed class SrtChunker
{
    public IReadOnlyList<SrtChunk> Chunk(
        IReadOnlyList<Subtitle> subtitles,
        ChunkStrategy strategy,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(subtitles);
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Chunk limit must be greater than zero.");
        }

        if (subtitles.Count == 0)
        {
            return [];
        }

        var chunks = new List<SrtChunk>();
        var current = new List<Subtitle>();
        var currentTokens = 0;

        foreach (var subtitle in subtitles)
        {
            var subtitleTokens = EstimateTokens(subtitle);
            var exceedsLimit = strategy switch
            {
                ChunkStrategy.MaxSubtitleCount => current.Count >= limit,
                ChunkStrategy.MaxTokens => current.Count > 0 && currentTokens + subtitleTokens > limit,
                _ => throw new ArgumentOutOfRangeException(nameof(strategy))
            };

            if (exceedsLimit)
            {
                chunks.Add(new SrtChunk(chunks.Count + 1, current.ToArray()));
                current.Clear();
                currentTokens = 0;
            }

            current.Add(subtitle);
            currentTokens += subtitleTokens;
        }

        if (current.Count > 0)
        {
            chunks.Add(new SrtChunk(chunks.Count + 1, current.ToArray()));
        }

        return chunks;
    }

    private static int EstimateTokens(Subtitle subtitle) =>
        Math.Max(1, (subtitle.Text.Length + 3) / 4);
}
