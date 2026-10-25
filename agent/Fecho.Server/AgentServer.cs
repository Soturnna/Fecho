using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Fecho.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Fecho.Server;

public sealed class AgentServerOptions
{
    public int Port { get; set; } = 47321;
    public required X509Certificate2 Certificate { get; init; }
    public required string AgentId { get; init; }
}

/// <summary>
/// Servidor WebSocket sobre TLS. Cada mensagem passa pelo Authenticator antes
/// de chegar ao CommandDispatcher.
/// </summary>
public sealed class AgentServer : IAsyncDisposable
{
    private const int MaxMessageBytes = 128 * 1024;

    private sealed class Connection
    {
        public required WebSocket Socket { get; init; }
        public ConnectionState State { get; } = new();
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private readonly AgentServerOptions _options;
    private readonly Authenticator _auth;
    private readonly CommandDispatcher _dispatcher;
    private readonly DeviceRegistry _registry;
    private readonly AuditLog _audit;
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private WebApplication? _app;

    public AgentServer(AgentServerOptions options, Authenticator auth, CommandDispatcher dispatcher,
        DeviceRegistry registry, AuditLog audit)
    {
        _options = options;
        _auth = auth;
        _dispatcher = dispatcher;
        _registry = registry;
        _audit = audit;
        _registry.Revoked += OnRevoked;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 4 * 1024;
            k.Limits.MaxConcurrentConnections = 16;
            k.ListenAnyIP(_options.Port, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = _options.Certificate;
                https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            }));
        });

        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        _app.Map("/ws", HandleAsync);
        _app.MapFallback(ctx => { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return Task.CompletedTask; });
        await _app.StartAsync();
    }

    public async Task StopAsync()
    {
        foreach (var c in _connections.Values)
        {
            try { c.Socket.Abort(); } catch (ObjectDisposedException) { }
        }
        if (_app is not null) await _app.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _registry.Revoked -= OnRevoked;
        await StopAsync();
        if (_app is not null) await _app.DisposeAsync();
    }

    /// <summary>Envia um evento (ex.: PC bloqueou) a todos os aparelhos autenticados.</summary>
    public async Task BroadcastAsync(string evt, object data)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { @event = evt, data }, Json.Options);
        foreach (var c in _connections.Values.Where(c => c.State.DeviceId is not null))
        {
            await SendAsync(c, bytes);
        }
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        if (!NetworkInfo.IsLocalAddress(ctx.Connection.RemoteIpAddress))
        {
            _audit.Record(null, "conexao", false, "endereço fora da rede local: " + ctx.Connection.RemoteIpAddress);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        var conn = new Connection { Socket = socket };
        _connections[id] = conn;
        try
        {
            await ReceiveLoopAsync(conn, ctx.RequestAborted);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            conn.State.Release();
            _connections.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync(Connection conn, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (conn.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await conn.Socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await conn.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    await conn.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, null, CancellationToken.None);
                    return;
                }
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            var reply = Process(message.ToArray(), conn.State);
            await SendAsync(conn, JsonSerializer.SerializeToUtf8Bytes(reply, Json.Options));
        }
    }

    /// <summary>Processa uma mensagem e devolve a resposta. Público para os testes.</summary>
    public Reply Process(byte[] raw, ConnectionState state)
    {
        Envelope? env;
        try { env = JsonSerializer.Deserialize<Envelope>(raw); }
        catch (JsonException) { env = null; }
        if (env is null) return new Reply { Ok = false, Error = ErrorCodes.Invalid };

        var id = env.Id.Length <= 64 ? env.Id : "";
        var auth = _auth.Authenticate(env, state);
        var cmd = auth.Payload?.Cmd ?? "?";
        if (!auth.Ok)
        {
            // Eventos rápidos recusados não entram no log para não afogá-lo.
            if (env.Mac is null) _audit.Record(auth.Payload?.Device, cmd, false, auth.Reason);
            return new Reply { Id = id, Ok = false, Error = auth.RateLimited ? ErrorCodes.RateLimited : ErrorCodes.Refused };
        }

        if (cmd == CommandPolicy.Pair)
        {
            _audit.Record(auth.Device!.Id, cmd, true, auth.Device.Name);
            return new Reply { Id = id, Ok = true, Result = new { paired = true } };
        }

        try
        {
            var result = _dispatcher.Execute(auth.Payload!, auth.Device!, state);
            if (env.Mac is null) _audit.Record(auth.Device!.Id, cmd, true);
            return new Reply { Id = id, Ok = true, Result = result };
        }
        catch (CommandException ex)
        {
            _audit.Record(auth.Device!.Id, cmd, false, ex.Message);
            return new Reply { Id = id, Ok = false, Error = ex.Code };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _audit.Record(auth.Device!.Id, cmd, false, ex.GetType().Name);
            return new Reply { Id = id, Ok = false, Error = ErrorCodes.Failed };
        }
    }

    private static async Task SendAsync(Connection c, byte[] bytes)
    {
        await c.SendLock.WaitAsync();
        try
        {
            if (c.Socket.State == WebSocketState.Open)
                await c.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            c.SendLock.Release();
        }
    }

    private void OnRevoked(string deviceId)
    {
        foreach (var c in _connections.Values.Where(c => c.State.DeviceId == deviceId || c.State.SessionDevice == deviceId))
        {
            c.State.EndSession();
            c.State.FilesGrantUntil = 0;
            try { c.Socket.Abort(); } catch (ObjectDisposedException) { }
        }
    }

    public static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
