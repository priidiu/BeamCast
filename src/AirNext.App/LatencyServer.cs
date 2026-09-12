using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AirNext_App;

/// <summary>
/// Local HTTP/WebSocket server (RHI-173) — exposes StreamMetrics to the MV3 extension.
/// GET /metrics → JSON (polling), GET /ws → WebSocket (push co ~0.8s).
/// Port: 46382 (fixed — the extension hard-codes it).
/// </summary>
public sealed class LatencyServer : IAsyncDisposable
{
    private const int Port = 46382;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private readonly List<WebSocket> _clients = new();
    private readonly object _clientsLock = new();
    private StreamMetrics? _latestMetrics;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public void Start()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token), CancellationToken.None);
        AppLog.Write($"LatencyServer: started on http://localhost:{Port}/");
    }

    public void UpdateMetrics(StreamMetrics metrics)
    {
        _latestMetrics = metrics;
        _ = BroadcastMetricsAsync();
    }

    public void Clear()
    {
        _latestMetrics = null;
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(ctx), CancellationToken.None);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                AppLog.Write($"LatencyServer: accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.IsWebSocketRequest && ctx.Request.Url?.AbsolutePath == "/ws")
            {
                await HandleWebSocketAsync(ctx).ConfigureAwait(false);
            }
            else if (ctx.Request.Url?.AbsolutePath == "/metrics")
            {
                await HandleMetricsAsync(ctx).ConfigureAwait(false);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"LatencyServer: request error: {ex.Message}");
            try { ctx.Response.Close(); } catch { }
        }
    }

    private async Task HandleMetricsAsync(HttpListenerContext ctx)
    {
        var json = BuildMetricsJson();
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private async Task HandleWebSocketAsync(HttpListenerContext ctx)
    {
        var wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var ws = wsCtx.WebSocket;

        lock (_clientsLock)
            _clients.Add(ws);

        AppLog.Write("LatencyServer: WebSocket client connected");

        // Send current metrics immediately
        if (_latestMetrics is { } m)
        {
            var json = BuildMetricsJson();
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }

        // Keep the connection until the client closes
        var buf = new byte[1];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch { }

        lock (_clientsLock)
            _clients.Remove(ws);

        AppLog.Write("LatencyServer: WebSocket client disconnected");
    }

    private async Task BroadcastMetricsAsync()
    {
        if (_latestMetrics is null) return;
        var json = BuildMetricsJson();
        var bytes = Encoding.UTF8.GetBytes(json);

        WebSocket[] clients;
        lock (_clientsLock)
            clients = _clients.ToArray();

        foreach (var ws in clients)
        {
            if (ws.State != WebSocketState.Open) continue;
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                lock (_clientsLock)
                    _clients.Remove(ws);
            }
        }
    }

    private string BuildMetricsJson()
    {
        var m = _latestMetrics;
        var data = new
        {
            isStreaming = m is not null,
            delayMs = m?.DelayMs ?? 0,
            totalLatencyMs = m?.DelayMs ?? 0,
            networkLatencyMs = m?.NetworkLatencyMs ?? -1,
            audioLatencyMs = m?.LatencyMs ?? 0,
            wasapiBufferMs = m?.WasapiBufferMs ?? 0,
            realTimeMode = m?.RealTimeMode ?? false,
            packetsPerSecond = m?.PacketsPerSecond ?? 0,
            glitchCount = m?.GlitchCount ?? 0,
        };
        return JsonSerializer.Serialize(data, JsonOpts);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        // Zamknij wszystkie WebSocket clients
        WebSocket[] clients;
        lock (_clientsLock)
        {
            clients = _clients.ToArray();
            _clients.Clear();
        }
        foreach (var ws in clients)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); } catch { }
            ws.Dispose();
        }

        _listener?.Stop();
        _listener?.Close();

        if (_listenerTask is not null)
        {
            try { await _listenerTask.ConfigureAwait(false); } catch { }
        }

        _cts?.Dispose();
        AppLog.Write("LatencyServer: stopped");
    }
}
