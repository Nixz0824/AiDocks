using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TwinDock.Services;

/// <summary>
/// Minimal loopback HTTP/1.1 stub used by the self-test to drive a real provider request
/// (credential discovery → request construction → status handling → parser) without TLS.
/// Captures the request line, headers and body so the test can assert on them.
/// </summary>
internal sealed class LoopbackStub : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public LoopbackStub(int statusCode, string body)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(() => ServeAsync(statusCode, body));
    }

    public int Port { get; }

    public string? LastRequestLine { get; private set; }

    public string? LastBody { get; private set; }

    public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }

    private async Task ServeAsync(int statusCode, string body)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

                LastRequestLine = await reader.ReadLineAsync(_cts.Token);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync(_cts.Token) is { Length: > 0 } line)
                {
                    var separator = line.IndexOf(':');
                    if (separator > 0)
                    {
                        headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                    }
                }

                LastHeaders.Clear();
                foreach (var header in headers)
                {
                    LastHeaders[header.Key] = header.Value;
                }

                if (headers.TryGetValue("Content-Length", out var rawLength) && int.TryParse(rawLength, out var length) && length > 0)
                {
                    var buffer = new char[length];
                    var read = 0;
                    while (read < length)
                    {
                        var chunk = await reader.ReadAsync(buffer.AsMemory(read, length - read), _cts.Token);
                        if (chunk == 0)
                        {
                            break;
                        }

                        read += chunk;
                    }

                    LastBody = new string(buffer, 0, read);
                }

                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {statusCode} {(statusCode == 200 ? "OK" : "Error")}\r\n" +
                    "Content-Type: application/json\r\n" +
                    $"Content-Length: {payload.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(head, _cts.Token);
                await stream.WriteAsync(payload, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
            {
                return;
            }
        }
    }
}

