using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TokenTracker.Core.Session;

namespace TokenTracker.Core.Tests;

public sealed class AhpTitleClientTests
{
    [Fact]
    public async Task ResolvesLiveTitleFromLocalAhpEndpoint()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var userData = Path.Combine(root, "user-data");
        var entries = Path.Combine(
            userData,
            "agent-host",
            "local-endpoint",
            "entries");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        using var serverCancellation = new CancellationTokenSource();
        Task? serverTask = null;

        try
        {
            Directory.CreateDirectory(entries);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            File.WriteAllText(
                Path.Combine(entries, "endpoint.json"),
                $$"""
                {
                  "schemaVersion": 2,
                  "type": "editor",
                  "pid": {{Environment.ProcessId}},
                  "instanceId": "test",
                  "protocolVersion": "1.1.0",
                  "connectionToken": "test-token",
                  "endpoint": { "type": "tcp", "host": "127.0.0.1", "port": {{port}} }
                }
                """);

            serverTask = Task.Run(
                () => ServeAsync(listener, serverCancellation.Token));

            var provider = new SessionMetadataProvider(
                root,
                Path.Combine(root, "missing-agent-host.db"),
                copilotUserDataDirectories: Array.Empty<string>(),
                ahpUserDataDirectories: new[] { userData });

            string? title = null;
            for (var attempt = 0; attempt < 50 && title is null; attempt++)
            {
                await Task.Delay(100);
                title = provider.Get("session-1")?.Title;
            }

            Assert.Equal("AHP live title", title);

            string? renamed = null;
            for (var attempt = 0; attempt < 50 && renamed != "AHP renamed title"; attempt++)
            {
                await Task.Delay(100);
                renamed = provider.Get("session-1")?.Title;
            }

            Assert.Equal("AHP renamed title", renamed);
        }
        finally
        {
            serverCancellation.Cancel();
            listener.Stop();
            if (serverTask is not null)
            {
                try
                {
                    await serverTask;
                }
                catch
                {
                }
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResolvesLiveTitleFromNamedPipeEndpoint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var userData = Path.Combine(root, "user-data");
        var entries = Path.Combine(
            userData,
            "agent-host",
            "local-endpoint",
            "entries");
        var pipeName = "tokentracker-ahp-" + Guid.NewGuid().ToString("N");
        var pipePath = @"\\.\pipe\" + pipeName;
        using var serverCancellation = new CancellationTokenSource();
        using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        Task? serverTask = null;

        try
        {
            Directory.CreateDirectory(entries);
            File.WriteAllText(
                Path.Combine(entries, "endpoint.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 2,
                    type = "editor",
                    pid = Environment.ProcessId,
                    instanceId = "test",
                    protocolVersion = "1.1.0",
                    connectionToken = "test-token",
                    endpoint = new { type = "socket", path = pipePath },
                }));

            serverTask = Task.Run(async () =>
            {
                await pipe.WaitForConnectionAsync(serverCancellation.Token);
                await ServeStreamAsync(pipe, serverCancellation.Token);
            });

            var provider = new SessionMetadataProvider(
                root,
                Path.Combine(root, "missing-agent-host.db"),
                copilotUserDataDirectories: Array.Empty<string>(),
                ahpUserDataDirectories: new[] { userData });

            string? title = null;
            for (var attempt = 0; attempt < 50 && title is null; attempt++)
            {
                await Task.Delay(100);
                title = provider.Get("session-1")?.Title;
            }

            Assert.Equal("AHP live title", title);
        }
        finally
        {
            serverCancellation.Cancel();
            pipe.Dispose();
            if (serverTask is not null)
            {
                try
                {
                    await serverTask;
                }
                catch
                {
                }
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task ServeAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        await ServeStreamAsync(stream, cancellationToken);
    }

    private static async Task ServeStreamAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var request = await ReadHeadersAsync(stream, cancellationToken);
        var key = request
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .First(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            .Split(':', 2)[1]
            .Trim();
        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(
                key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);

        using var socket = WebSocket.CreateFromStream(
            stream,
            isServer: true,
            subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(20));
        while (!cancellationToken.IsCancellationRequested &&
               socket.State == WebSocketState.Open)
        {
            var text = await ReceiveTextAsync(socket, cancellationToken);
            if (text is null)
            {
                return;
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) ||
                !idElement.TryGetInt64(out var id) ||
                !root.TryGetProperty("method", out var methodElement))
            {
                continue;
            }

            var method = methodElement.GetString();
            var result = method switch
            {
                "initialize" => "{\"protocolVersion\":\"1.1.0\"}",
                "listSessions" => """
                    {"items":[{"resource":"ahp-session:/session-1","title":"AHP live title","workingDirectories":["file:///d:/source/demo"]}]}
                    """,
                _ => "{}",
            };
            await SendTextAsync(
                socket,
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}",
                cancellationToken);
            if (method == "listSessions")
            {
                await Task.Delay(200, cancellationToken);
                await SendTextAsync(
                    socket,
                    """
                    {"jsonrpc":"2.0","method":"action","params":{"channel":"ahp-session:/session-1","action":{"type":"session/titleChanged","title":"AHP renamed title"},"serverSeq":2}}
                    """,
                    cancellationToken);
            }
        }
    }

    private static async Task<string> ReadHeadersAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();
        while (builder.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return builder.ToString();
            }
        }

        throw new InvalidOperationException("handshake closed");
    }

    private static async Task<string?> ReceiveTextAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    private static Task SendTextAsync(
        WebSocket socket,
        string text,
        CancellationToken cancellationToken) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(text),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
}
