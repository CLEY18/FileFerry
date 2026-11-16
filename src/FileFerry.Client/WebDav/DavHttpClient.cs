using System.Net;

namespace FileFerry.Client.WebDav;

/// <summary>
/// <see cref="HttpClient"/> factory for WebDAV: always the managed <see cref="SocketsHttpHandler"/>.
/// Android platform fact: the native handler (Java <c>HttpURLConnection</c>) rejects verbs outside
/// its own list — PROPFIND/MKCOL/MOVE die with a <c>WebException</c> "Expected one of [OPTIONS, GET, …]"
/// before any network I/O. The managed stack sends arbitrary method tokens.
/// </summary>
public static class DavHttpClient
{
    /// <summary>
    /// Client with a base address; the timeout is for quick probes (DAV runs keep the default, no shortening).
    /// </summary>
    public static HttpClient Create(Uri baseAddress, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false, // authentication is stateless Basic; DAV needs no cookie jar
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { BaseAddress = baseAddress };
        if (timeout is not null)
        {
            client.Timeout = timeout.Value;
        }

        return client;
    }
}
