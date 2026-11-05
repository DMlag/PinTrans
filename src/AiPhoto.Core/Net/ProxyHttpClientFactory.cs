using System.Net;

namespace AiPhoto.Core.Net;

public enum ProxyMode
{
    /// <summary>Direct connection.</summary>
    None,
    /// <summary>Use the operating system (Internet Options) proxy.</summary>
    System,
    Http,
    Socks5,
}

public sealed record ProxySettings
{
    public ProxyMode Mode { get; init; } = ProxyMode.System;
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 7890;
    public string? Username { get; init; }
    public string? Password { get; init; }

    // UriBuilder adds the brackets IPv6 literals need ("::1" -> "[::1]"); string interpolation produced an
    // invalid URI and threw UriFormatException.
    public Uri? ToUri() => Mode switch
    {
        ProxyMode.Http => new UriBuilder("http", Host.Trim(), Port).Uri,
        ProxyMode.Socks5 => new UriBuilder("socks5", Host.Trim(), Port).Uri,
        _ => null,
    };
}

public static class ProxyHttpClientFactory
{
    public static HttpClient Create(ProxySettings proxy, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        switch (proxy.Mode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;
            case ProxyMode.System:
                handler.UseProxy = true; // Proxy == null => HttpClient.DefaultProxy (system settings)
                break;
            case ProxyMode.Http:
            case ProxyMode.Socks5:
                if (string.IsNullOrWhiteSpace(proxy.Host) || proxy.Port is <= 0 or > 65535)
                    throw new ArgumentException("代理地址或端口无效。");
                Uri proxyUri;
                try
                {
                    proxyUri = proxy.ToUri()!;
                }
                catch (UriFormatException ex)
                {
                    // Keep the documented contract: invalid proxy settings => ArgumentException.
                    throw new ArgumentException("代理地址或端口无效。", ex);
                }
                var webProxy = new WebProxy(proxyUri);
                if (!string.IsNullOrEmpty(proxy.Username))
                    webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password ?? "");
                handler.Proxy = webProxy;
                handler.UseProxy = true;
                break;
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(120),
        };
    }
}
