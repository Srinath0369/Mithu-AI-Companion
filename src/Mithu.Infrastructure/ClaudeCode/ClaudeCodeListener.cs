using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Mithu.Infrastructure.ClaudeCode;

/// <summary>
/// A tiny local listener for Claude Code's hooks: 127.0.0.1 only, one path with a secret token, POST bodies up to
/// 1 MB. Each accepted body is handed to <see cref="Received"/> (on a background thread). Anything else gets 404.
/// Never logs bodies (they hold prompts, paths and commands).
/// </summary>
public sealed class ClaudeCodeListener(ILogger<ClaudeCodeListener> logger) : IDisposable
{
    private const int MaxBody = 1024 * 1024;
    private static readonly byte[] Ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
    private static readonly byte[] NotFound = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private string _path = string.Empty;

    /// <summary>An event's JSON body arrived (background thread).</summary>
    public event Action<string>? Received;

    public bool IsRunning => _listener is not null;

    /// <summary>Starts listening on the port for <paramref name="path"/>; false when the port is taken.</summary>
    public bool Start(int port, string path)
    {
        Stop();
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            _listener = listener;
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "Claude Code status: port {Port} is in use", port);
            return false;
        }

        _path = path;
        _stop = new CancellationTokenSource();
        _ = AcceptLoopAsync(_listener, _stop.Token);
        logger.LogInformation("Claude Code status: listening on 127.0.0.1:{Port}", port);
        return true;
    }

    public void Stop()
    {
        _stop?.Cancel();
        _listener?.Stop();
        _listener = null;
        _stop = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = HandleAsync(client, token);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var stream = client.GetStream();
                var (head, body) = await ReadRequestAsync(stream, timeout.Token);
                var accepted = head is not null && body is not null
                    && head.StartsWith("POST " + _path + " ", StringComparison.Ordinal);
                await stream.WriteAsync(accepted ? Ok : NotFound, timeout.Token);
                if (accepted)
                {
                    Received?.Invoke(body!);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // A client that went away or was too slow: nothing to do.
            }
        }
    }

    /// <summary>Reads the request line + headers and the body (by Content-Length).</summary>
    private static async Task<(string? Head, string? Body)> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        var data = new MemoryStream();
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0 || data.Length > 16 * 1024)
            {
                return (null, null);
            }

            data.Write(buffer, 0, read);
            headerEnd = IndexOf(data.GetBuffer(), (int)data.Length, "\r\n\r\n"u8);
        }

        var head = Encoding.ASCII.GetString(data.GetBuffer(), 0, headerEnd);
        var length = 0;
        foreach (var line in head.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && int.TryParse(line[15..].Trim(), out var n))
            {
                length = n;
            }
        }

        if (length is < 0 or > MaxBody)
        {
            return (head, null);
        }

        var body = new byte[length];
        var have = (int)data.Length - (headerEnd + 4);
        Array.Copy(data.GetBuffer(), headerEnd + 4, body, 0, Math.Min(have, length));
        while (have < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(have, length - have), token);
            if (read == 0)
            {
                return (head, null);
            }

            have += read;
        }

        return (head, Decode(body));
    }

    /// <summary>
    /// UTF-8, as Claude Code sends it; UTF-16 too (a byte-order mark, or zero bytes between ASCII characters), which
    /// some shells produce when text is piped into curl.
    /// </summary>
    internal static string Decode(byte[] body)
    {
        if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(body, 2, body.Length - 2);
        }

        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(body, 3, body.Length - 3);
        }

        return body.Length >= 4 && body[1] == 0 && body[3] == 0 ? Encoding.Unicode.GetString(body) : Encoding.UTF8.GetString(body);
    }

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern) =>
        data.AsSpan(0, length).IndexOf(pattern);

    public void Dispose() => Stop();
}
