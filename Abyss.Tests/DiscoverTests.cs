using System.Net;
using System.Text;
using Abyss.Components.Services.Misc;
using Abyss.Components.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Abyss.Tests;

public class DiscoverTests
{
    [Fact]
    public void CryptoVectorMatches()
    {
        DiscoverChannel.AssertTestVector();
    }

    [Theory]
    [InlineData("abyssc://broker.emqx.io:kitchen/", "broker.emqx.io", 1883, "kitchen")]
    [InlineData("abyssc://broker.emqx.io:8883:kitchen/", "broker.emqx.io", 8883, "kitchen")]
    [InlineData("abyssc://[2001:db8::1]:kitchen/", "2001:db8::1", 1883, "kitchen")]
    [InlineData("abyssc://[2001:db8::1]:8883:kitchen/", "2001:db8::1", 8883, "kitchen")]
    public void ParsesSampleUrls(string raw, string host, int port, string magic)
    {
        Assert.True(DiscoverChannel.TryParse(raw, out var endpoint));
        Assert.Equal(host, endpoint.Host);
        Assert.Equal(port, endpoint.Port);
        Assert.Equal(magic, endpoint.Magic);
    }

    [Fact]
    public void RejectsMagicThatContainsAColon()
    {
        Assert.False(DiscoverChannel.TryParse("abyssc://broker.emqx.io:kit:chen/", out _));
    }

    [Fact]
    public void OfferJsonRoundTrips()
    {
        var json = DiscoverChannel.OfferJson("00112233445566778899aabbccddeeff", "http://[2001:db8::1]:80/");
        var obj = JObject.Parse(json);
        Assert.Equal(1, obj.Value<int>("v"));
        Assert.Equal("offer", obj.Value<string>("op"));
        Assert.Equal("http://[2001:db8::1]:80/", obj.Value<string>("url"));
        Assert.Equal(
            DiscoverChannel.TestPlain,
            DiscoverChannel.HelloJson("00112233445566778899aabbccddeeff"));
    }

    [Fact]
    public void TopicIsSha256OfMagic()
    {
        Assert.Equal(
            "abyss/c/3171d89ad00530ffa19a244f040e9401a657903cbbbca724996b90a56df2c189",
            DiscoverChannel.Topic("kitchen"));
    }

    [Fact]
    public void MissingFileKeepsDefaultsAndDoesNotCreate()
    {
        var path = Path.Combine(Path.GetTempPath(), "abyss-missing-" + Guid.NewGuid().ToString("N"), "abyss.conf");
        var config = new ConfigureService(path, createIfMissing: false);
        Assert.False(File.Exists(path));
        Assert.Equal(ConfigureService.DefaultMediaRoot, config.MediaRoot);
        Assert.Equal(ConfigureService.DefaultDebugMode, config.DebugMode);
        Assert.Equal(ConfigureService.DefaultAllowedPorts, config.AllowedPorts);
        Assert.Equal(ConfigureService.DefaultUrls, config.Urls);
        Assert.Equal("", config.Discover);
        Assert.Equal("", config.PublicUrl);
    }

    [Fact]
    public void CreatesDefaultFileOnceAndKeepsEmptyValues()
    {
        var dir = Path.Combine(Path.GetTempPath(), "abyss-conf-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "abyss.conf");
        try
        {
            var created = new ConfigureService(path, createIfMissing: true);
            Assert.Equal(ConfigureService.DefaultBody, File.ReadAllText(path));
            Assert.Equal(ConfigureService.DefaultMediaRoot, created.MediaRoot);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }

            File.WriteAllText(path,
                "media_root = /opt\n" +
                "debug_mode =\n" +
                "allowed_ports =\n" +
                "urls =\n" +
                "discover =\n" +
                "public_url =\n" +
                "# ignored\n" +
                "unknown = 1\n");
            var loaded = new ConfigureService(path, createIfMissing: true);
            Assert.Equal("/opt", loaded.MediaRoot);
            Assert.Equal("", loaded.DebugMode);
            Assert.Equal("", loaded.AllowedPorts);
            Assert.Equal("", loaded.Urls);
            Assert.Equal("", loaded.Discover);
            Assert.Equal("", loaded.PublicUrl);
            Assert.Contains("media_root = /opt", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ExpandListenUrlsPairsTheMissingFamily()
    {
        Assert.Equal(
            "http://0.0.0.0:80;http://[::]:80",
            ConfigureService.ExpandListenUrls("http://0.0.0.0:80"));
        Assert.Equal(
            "https://[::]:443;https://0.0.0.0:443",
            ConfigureService.ExpandListenUrls("https://[::]:443"));
        Assert.Equal(
            "http://0.0.0.0:80;http://[::]:80",
            ConfigureService.ExpandListenUrls("http://0.0.0.0:80;http://[::]:80"));
        Assert.Equal(
            "http://127.0.0.1:8080",
            ConfigureService.ExpandListenUrls("http://127.0.0.1:8080"));
        Assert.Equal(
            ConfigureService.DefaultUrls,
            ConfigureService.ExpandListenUrls(""));
    }

    [Fact]
    public void ChoosePrefersGlobalIpv6WhenBothFamiliesListen()
    {
        var chosen = PublicAddress.Choose(
            [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2001:db8::2"), IPAddress.Parse("2001:db8::1")],
            ["http://0.0.0.0:80", "http://[::]:80"]);
        Assert.Equal("http://[2001:db8::1]:80/", chosen);
    }

    [Fact]
    public void ChooseStaysOnTheFamilyThatIsListening()
    {
        var chosen = PublicAddress.Choose(
            [IPAddress.Parse("2001:db8::1"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("192.168.1.5")],
            ["http://0.0.0.0:80"]);
        Assert.Equal("http://8.8.8.8:80/", chosen);
    }

    [Fact]
    public void ChoosePrefersHttpsAndRanksPrivateBelowPublic()
    {
        var https = PublicAddress.Choose(
            [IPAddress.Parse("2001:db8::1")],
            ["http://[::]:80", "https://[::]:443"]);
        Assert.Equal("https://[2001:db8::1]:443/", https);

        var ula = PublicAddress.Choose(
            [IPAddress.Parse("10.1.2.3"), IPAddress.Parse("fd00::1")],
            ["http://0.0.0.0:80", "http://[::]:80"]);
        Assert.Equal("http://[fd00::1]:80/", ula);

        Assert.Null(PublicAddress.Rank(IPAddress.Parse("169.254.1.1")));
        Assert.Null(PublicAddress.Rank(IPAddress.Parse("fe80::1")));
        Assert.Null(PublicAddress.Rank(IPAddress.Loopback));
        Assert.Equal(0, PublicAddress.Rank(IPAddress.Parse("2001:db8::1")));
        Assert.Equal(1, PublicAddress.Rank(IPAddress.Parse("8.8.8.8")));
        Assert.Equal(2, PublicAddress.Rank(IPAddress.Parse("fd00::1")));
        Assert.Equal(3, PublicAddress.Rank(IPAddress.Parse("192.168.0.2")));
        Assert.Equal(3, PublicAddress.Rank(IPAddress.Parse("100.64.0.1")));
    }

    [Fact]
    public void AbyssOfferUsesGlobalIpv6AndLoopbackHttpPort()
    {
        var chosen = PublicAddress.AbyssUrl(
            [
                IPAddress.Parse("192.168.1.50"),
                IPAddress.Parse("2408:8234:1e1b:6d9:2ecf:67ff:feab:2668"),
                IPAddress.Parse("fe80::2ecf:67ff:feab:2668")
            ],
            ["http://[::1]:80", "https://[::1]:443"]);
        Assert.Equal("abyss://[2408:8234:1e1b:6d9:2ecf:67ff:feab:2668]:80/", chosen);
        Assert.Null(PublicAddress.AbyssUrl(
            [IPAddress.Parse("192.168.1.50")],
            ["http://[::1]:80"]));
    }

    [Fact]
    public void WrongMagicDoesNotOpen()
    {
        var packet = DiscoverChannel.Seal(Encoding.UTF8.GetBytes("hello"), "kitchen");
        Assert.Null(DiscoverChannel.Open(packet, "pantry"));
    }
}
