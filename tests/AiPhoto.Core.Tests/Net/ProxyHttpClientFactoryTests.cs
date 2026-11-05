using System.Net;
using AiPhoto.Core.Net;

namespace AiPhoto.Core.Tests.Net;

public class ProxyHttpClientFactoryTests
{
    [Theory]
    [InlineData(ProxyMode.None)]
    [InlineData(ProxyMode.System)]
    [InlineData(ProxyMode.Http)]
    [InlineData(ProxyMode.Socks5)]
    public void Create_WorksForEveryMode(ProxyMode mode)
    {
        using var client = ProxyHttpClientFactory.Create(new ProxySettings { Mode = mode, Host = "127.0.0.1", Port = 7890 });
        Assert.Equal(TimeSpan.FromSeconds(120), client.Timeout);
    }

    [Fact]
    public void Create_WithCredentialsAndTimeout()
    {
        using var client = ProxyHttpClientFactory.Create(
            new ProxySettings { Mode = ProxyMode.Http, Host = "proxy.local", Port = 3128, Username = "u", Password = "p" },
            TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }

    [Theory]
    [InlineData(ProxyMode.Http, "127.0.0.1", 0)]
    [InlineData(ProxyMode.Http, "127.0.0.1", -1)]
    [InlineData(ProxyMode.Socks5, "127.0.0.1", 65536)]
    [InlineData(ProxyMode.Http, "", 8080)]
    [InlineData(ProxyMode.Socks5, "   ", 1080)]
    [InlineData(ProxyMode.Http, "bad host name", 8080)]
    public void Create_InvalidProxy_ThrowsArgumentException(ProxyMode mode, string host, int port)
    {
        Assert.ThrowsAny<ArgumentException>(() => ProxyHttpClientFactory.Create(new ProxySettings { Mode = mode, Host = host, Port = port }));
    }

    [Theory]
    [InlineData(ProxyMode.None)]
    [InlineData(ProxyMode.System)]
    public void Create_InvalidPortIsIgnoredWhenNoExplicitProxy(ProxyMode mode)
    {
        using var client = ProxyHttpClientFactory.Create(new ProxySettings { Mode = mode, Port = -5 });
        Assert.NotNull(client);
    }

    [Fact]
    public void ToUri()
    {
        Assert.Equal(new Uri("http://1.2.3.4:8080"), new ProxySettings { Mode = ProxyMode.Http, Host = "1.2.3.4", Port = 8080 }.ToUri());
        Assert.Equal("socks5", new ProxySettings { Mode = ProxyMode.Socks5, Host = "h", Port = 1 }.ToUri()!.Scheme);
        Assert.Null(new ProxySettings { Mode = ProxyMode.System }.ToUri());
        Assert.Null(new ProxySettings { Mode = ProxyMode.None }.ToUri());
    }

    [Fact]
    public void ToUri_Ipv6Host_IsBracketed()
    {
        var uri = new ProxySettings { Mode = ProxyMode.Http, Host = "::1", Port = 8080 }.ToUri()!;
        Assert.Equal(8080, uri.Port);
        Assert.Equal(IPAddress.IPv6Loopback.ToString(), uri.Host.Trim('[', ']'));
    }
}
