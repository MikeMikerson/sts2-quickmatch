using QuickMatchRelay.Relay;
using Xunit;

namespace QuickMatchRelay.Tests;

public class SettingsTests
{
    private static (RelaySettings Settings, List<string> Problems) Parse(string? text)
    {
        var problems = new List<string>();
        return (RelaySettings.Parse(text, problems.Add), problems);
    }

    [Fact]
    public void WithoutAFileOrARelayTheFastLinkIsOff()
    {
        var (settings, problems) = Parse(null);

        Assert.False(settings.Enabled);
        Assert.Null(settings.Relay);
        Assert.Empty(problems);
        Assert.False(Parse("enabled = true").Settings.Enabled);
    }

    [Fact]
    public void TheFastLinkCanBeTurnedOff()
    {
        Assert.False(Parse("enabled = false").Settings.Enabled);
        Assert.False(Parse("# my settings\r\n  Enabled=off\r\n").Settings.Enabled);
        Assert.True(Parse("enabled = yes\nrelay = wss://relay.example.org").Settings.Enabled);
    }

    [Fact]
    public void AnotherRelayCanBeChosen()
    {
        var (settings, problems) = Parse("""
            # my own relay
            relay = https://my-relay.someone.workers.dev/
            """);

        Assert.True(settings.Enabled);
        Assert.Equal(new Uri("wss://my-relay.someone.workers.dev"), settings.Relay);
        Assert.Empty(problems);
        Assert.Equal(new Uri("wss://relay.example.org"), Parse("relay = wss://relay.example.org:443").Settings.Relay);
    }

    [Theory]
    [InlineData("relay = http://relay.example.org", "https://")]
    [InlineData("relay = ws://relay.example.org", "https://")]
    [InlineData("relay = https://relay.example.org:8443", "port 443")]
    [InlineData("relay = https://relay.example.org/v1/relay/apac", "without a path")]
    [InlineData("relay = https://relay.example.org/?x=1", "without a path")]
    [InlineData("relay = https://me:secret@relay.example.org", "user name")]
    [InlineData("relay = https://203.0.113.7", "not an IP address")]
    [InlineData("relay = https://[2001:db8::1]", "not an IP address")]
    [InlineData("relay = https://localhost", "not an IP address")]
    [InlineData("relay = relay.example.org", "not an address")]
    [InlineData("enabled = maybe", "true or false")]
    [InlineData("speed = fast", "unknown setting")]
    [InlineData("relay", "key = value")]
    [InlineData("enabled = true\nenabled = false", "set twice")]
    public void AnyProblemTurnsTheFastLinkOff(string text, string reason)
    {
        var (settings, problems) = Parse(text);

        Assert.False(settings.Enabled);
        Assert.Contains(problems, p => p.Contains(reason) && p.Contains(RelaySettings.FileName));
    }
}
