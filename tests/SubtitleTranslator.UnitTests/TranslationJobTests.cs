using SubtitleTranslator.Domain.Entities;
using SubtitleTranslator.Domain.Enums;

namespace SubtitleTranslator.UnitTests;

public class TranslationJobTests
{
    [Fact]
    public void NewJob_DefaultsToDiscoveredStatus()
    {
        var job = new TranslationJob { SourcePath = "movie.srt" };

        Assert.Equal(JobStatus.Discovered, job.Status);
        Assert.Equal(0, job.TotalChunks);
        Assert.Equal(0, job.RetryCount);
    }

    [Fact]
    public void NewChunk_DefaultsToPendingStatus()
    {
        var chunk = new TranslationChunk { JobId = 1 };

        Assert.Equal(ChunkStatus.Pending, chunk.Status);
        Assert.Equal(0, chunk.Attempt);
    }
}
