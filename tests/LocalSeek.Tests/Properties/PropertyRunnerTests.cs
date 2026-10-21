namespace LocalSeek.Tests.Properties;

public class PropertyRunnerTests
{
    [Theory]
    [InlineData("123", 123)]
    [InlineData("-7", -7)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    public void Replay_seed_is_parsed_from_the_environment_value(string? value, int? expected)
    {
        Assert.Equal(expected, Property.ParseReplaySeed(value));
    }

    [Fact]
    public void Coverage_shortfall_fails_a_normal_run()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Property.AssertCoverage(false, "Only 1 checked.", replay: false));
        Assert.Contains("Only 1 checked.", ex.Message);
    }

    [Fact]
    public void Coverage_shortfall_is_ignored_when_replaying_one_case()
    {
        Property.AssertCoverage(false, "Only 1 checked.", replay: true);
    }

    [Fact]
    public void Coverage_met_passes_a_normal_run()
    {
        Property.AssertCoverage(true, "unused", replay: false);
    }
}
