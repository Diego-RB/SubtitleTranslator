using SubtitleTranslator.Srt;
using SubtitleTranslator.Srt.Parser;
using SubtitleTranslator.Srt.Writer;

namespace SubtitleTranslator.UnitTests;

public class SrtFileServiceTests
{
    [Fact]
    public async Task ReadAndWrite_UsesUtf8SrtFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"subtitle-{Guid.NewGuid():N}.srt");
        try
        {
            var service = new SrtFileService(new SrtParser(), new SrtWriter());
            await service.WriteAsync(path, [new()
            {
                Index = 1,
                Start = TimeSpan.Zero,
                End = TimeSpan.FromSeconds(1),
                Text = "Olá"
            }]);

            var subtitles = await service.ReadAsync(path);
            Assert.Equal("Olá", Assert.Single(subtitles).Text);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
