using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using OsuCompanion.Models;

namespace OsuCompanion.Network;

public sealed class GameplayWebSocketServer : IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(16);
    private const int SendTimeoutMs = 250;

    private readonly HttpListener _listener;
    private readonly string _staticRoot;
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly object _snapshotGate = new();

    private GameplayData _snapshot = new();
    private CancellationTokenSource? _cts;

    public int Port { get; }

    public GameplayWebSocketServer(int port, string staticRoot)
    {
        Port = port;
        _staticRoot = staticRoot;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void UpdateSnapshot(GameplayData data)
    {
        lock (_snapshotGate)
        {
            _snapshot = data;
        }
    }

    public async Task StartAsync(CancellationToken token)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _listener.Start();

        var broadcast = Task.Run(() => BroadcastLoopAsync(_cts.Token));

        while (!_cts.Token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_cts.Token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                continue;
            }

            _ = Task.Run(() => HandleContextAsync(context, _cts.Token));
        }

        await broadcast.ConfigureAwait(false);
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            if (context.Request.IsWebSocketRequest)
            {
                var webSocketContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                var id = Guid.NewGuid();
                _clients[id] = webSocketContext.WebSocket;

                await ReceiveLoopAsync(webSocketContext.WebSocket, token).ConfigureAwait(false);
                _clients.TryRemove(id, out _);
            }
            else
            {
                await ServeStaticAsync(context).ConfigureAwait(false);
            }
        }
        catch
        {
            try
            {
                context.Response.Abort();
            }
            catch
            {
            }
        }
    }

    private static async Task ReceiveLoopAsync(WebSocket webSocket, CancellationToken token)
    {
        var buffer = new byte[1024];
        try
        {
            while (webSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                byte[] payload;
                lock (_snapshotGate)
                {
                    payload = JsonSerializer.SerializeToUtf8Bytes(_snapshot);
                }

                var sends = new List<Task>(_clients.Count);
                foreach (var (id, webSocket) in _clients.ToArray())
                {
                    if (webSocket.State != WebSocketState.Open)
                    {
                        _clients.TryRemove(id, out _);
                        continue;
                    }

                    sends.Add(SendAsync(id, webSocket, payload));
                }

                // Sends run concurrently and each is bounded by a timeout, so one stalled
                // browser tab can never freeze the 16 ms stream for every other client.
                if (sends.Count > 0)
                {
                    await Task.WhenAll(sends).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendAsync(Guid id, WebSocket webSocket, byte[] payload)
    {
        using var timeout = new CancellationTokenSource(SendTimeoutMs);
        try
        {
            await webSocket
                .SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            _clients.TryRemove(id, out _);
            try
            {
                webSocket.Abort();
            }
            catch
            {
            }
        }
    }

    private async Task ServeStaticAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        if (path == "/")
        {
            path = "/index.html";
        }

        var staticRoot = Path.GetFullPath(_staticRoot);
        var filePath = Path.GetFullPath(Path.Combine(staticRoot, path.TrimStart('/')));

        if (!filePath.StartsWith(staticRoot, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }

        if (!File.Exists(filePath))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        var bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
        context.Response.ContentType = GetContentType(filePath);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        foreach (var webSocket in _clients.Values)
        {
            try
            {
                webSocket.Dispose();
            }
            catch
            {
            }
        }

        _clients.Clear();

        try
        {
            _listener.Close();
        }
        catch
        {
        }
    }
}
