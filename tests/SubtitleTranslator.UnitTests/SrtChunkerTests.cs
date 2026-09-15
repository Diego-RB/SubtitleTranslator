using SubtitleTranslator.Domain.Enums;
using SubtitleTranslator.Domain.ValueObjects;
using SubtitleTranslator.Srt.Chunking;

namespace SubtitleTranslator.UnitTests;

public class SrtChunkerTests
{
    [Fact]
    public void Chunk_BySubtitleCountDoesNotSplitSubtitle()
    {
        var subtitles = Enumerable.Range(1, 5)
            .Select(index => new Subtitle
            {
                Index = index,
                Start = TimeSpan.FromSeconds(index),
                End = TimeSpan.FromSeconds(index + 1),
                Text = $"Subtitle {index}"
            })
            .ToArray();

        var chunks = new SrtChunker().Chunk(subtitles, ChunkStrategy.MaxSubtitleCount, 2);

        Assert.Equal(3, chunks.Count);
        Assert.Equal([1, 2], chunks[0].Subtitles.Select(s => s.Index));
        Assert.Equal([3, 4], chunks[1].Subtitles.Select(s => s.Index));
        Assert.Equal([5], chunks[2].Subtitles.Select(s => s.Index));
        Assert.Equal(1, chunks[0].Sequence);
        Assert.Equal(5, chunks[2].EndSubtitleIndex);
    }

    [Fact]
    public void Chunk_ByTokensStartsNewChunkWhenLimitWouldBeExceeded()
    {
        var subtitles = new[]
        {
            Subtitle(1, "12345678"),
            Subtitle(2, "abcdefgh")
        };

        var chunks = new SrtChunker().Chunk(subtitles, ChunkStrategy.MaxTokens, 2);

        Assert.Equal(2, chunks.Count);
        Assert.Single(chunks[0].Subtitles);
        Assert.Equal(2, chunks[1].Subtitles.Single().Index);
    }

    [Fact]
    public void Chunk_RejectsNonPositiveLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SrtChunker().Chunk([], ChunkStrategy.MaxTokens, 0));
    }

    private static Subtitle Subtitle(int index, string text) => new()
    {
        Index = index,
        Start = TimeSpan.FromSeconds(index),
        End = TimeSpan.FromSeconds(index + 1),
        Text = text
    };
}
