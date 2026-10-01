using SubtitleTranslator.Infrastructure.Security;

namespace SubtitleTranslator.UnitTests;

public class DashboardAccessTokenValidatorTests
{
    [Fact]
    public void IsValid_ReturnsTrue_ForExactMatch()
    {
        Assert.True(DashboardAccessTokenValidator.IsValid("token-value", "token-value"));
    }

    [Theory]
    [InlineData("wrong-value", "token-value")]
    [InlineData("short", "longer-configured-token")]
    [InlineData("", "token-value")]
    [InlineData(null, "token-value")]
    [InlineData("token-value", null)]
    public void IsValid_ReturnsFalse_WhenTokenDoesNotMatch(string? supplied, string? configured)
    {
        Assert.False(DashboardAccessTokenValidator.IsValid(supplied, configured));
    }
}
