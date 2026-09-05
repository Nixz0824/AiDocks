using System.Net;
using System.Net.Http;

namespace QuotaDock.Services;

internal static class OutboundHttp
{
    public static HttpClient Create(TimeSpan timeout, string userAgent)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = timeout,
            Proxy = new LiveLocalProxy(),
            UseProxy = true
        };
        var http = new HttpClient(handler) { Timeout = timeout };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        return http;
    }
}
