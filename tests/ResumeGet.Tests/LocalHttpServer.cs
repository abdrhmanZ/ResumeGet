using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ResumeGet.Tests;

internal sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<Task> connections = [];
    private readonly Func<TestHttpRequest, TestHttpReply> replyFactory;
    private readonly Task acceptLoop;

    public LocalHttpServer(Func<TestHttpRequest, TestHttpReply> replyFactory)
    {
        this.replyFactory = replyFactory;
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Address = new Uri($"http://127.0.0.1:{port}/sample.bin");
        acceptLoop = AcceptConnectionsAsync();
    }

    public Uri Address { get; }

    private async Task AcceptConnectionsAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var connection = await listener.AcceptTcpClientAsync(shutdown.Token);
                connections.Add(ServeAsync(connection));
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (SocketException) when (shutdown.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient connection)
    {
        using (connection)
        {
            try
            {
                var stream = connection.GetStream();
                var request = await ReadRequestAsync(stream, shutdown.Token);
                var reply = replyFactory(request);
                await WriteReplyAsync(stream, reply, shutdown.Token);
            }
            // Cancellation tests intentionally close the peer before a response finishes.
            catch (IOException) { }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        }
    }

    private static async Task<TestHttpRequest> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        if (await reader.ReadLineAsync(token) is null)
            throw new IOException("Missing request line.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
        {
            var separator = line.IndexOf(':');
            headers.Add(line[..separator], line[(separator + 1)..].Trim());
        }
        return new TestHttpRequest(headers);
    }

    private static async Task WriteReplyAsync(Stream stream, TestHttpReply reply, CancellationToken token)
    {
        var headers = new StringBuilder($"HTTP/1.1 {reply.Status} Test\r\nConnection: close\r\n");
        if (reply.ContentLength is { } length)
            headers.Append($"Content-Length: {length}\r\n");
        foreach (var (name, headerValue) in reply.Headers)
            headers.Append($"{name}: {headerValue}\r\n");
        headers.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), token);
        await stream.WriteAsync(reply.Body, token);
        await stream.FlushAsync(token);
        if (reply.WaitAfterBody is { } wait)
            await wait.WaitAsync(token);
    }

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync();
        listener.Stop();
        await acceptLoop;
        await Task.WhenAll(connections);
        shutdown.Dispose();
    }
}

internal sealed record TestHttpRequest(IReadOnlyDictionary<string, string> Headers)
{
    public long? RangeStart => Headers.TryGetValue("Range", out var range)
        ? long.Parse(range["bytes=".Length..].TrimEnd('-'), System.Globalization.CultureInfo.InvariantCulture)
        : null;
}

internal sealed record TestHttpReply(int Status, byte[] Body, long? ContentLength)
{
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Task? WaitAfterBody { get; init; }
}
