using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Notch.Models;

namespace Notch.Services;

/// <summary>
/// Lokaler HTTP-Endpunkt (nur Loopback, Token-geschützt).
///   GET  /health                 ohne Token
///   POST /event                  EventDto als JSON
///   POST /dismiss                {source?, id?}
///   GET  /result?source=..&id=.. gewählte Aktion (200) oder nichts (204)
/// </summary>
public sealed class EventServer : IDisposable
{
    private const long MaxBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpListener _listener = new();
    private readonly EventResolver _resolver;
    private readonly Dispatcher _ui;
    private readonly byte[] _token;

    public EventServer(int port, string token, EventResolver resolver, Dispatcher ui)
    {
        _resolver = resolver;
        _ui = ui;
        _token = Encoding.UTF8.GetBytes(token);
        // "localhost" ist ohne Adminrechte registrierbar und bindet nur an Loopback
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";

            if (req.HttpMethod == "GET" && path == "/health")
            {
                await WriteAsync(res, 200, new { ok = true });
                return;
            }
            if (!IsAuthorized(req))
            {
                await WriteAsync(res, 401, new { error = "unauthorized" });
                return;
            }
            if (req.ContentLength64 > MaxBodyBytes)
            {
                await WriteAsync(res, 413, new { error = "body too large" });
                return;
            }

            switch (req.HttpMethod, path)
            {
                case ("POST", "/event"):
                {
                    var dto = await JsonSerializer.DeserializeAsync<EventDto>(req.InputStream, Json);
                    if (dto is null)
                    {
                        await WriteAsync(res, 400, new { error = "body fehlt" });
                        break;
                    }
                    var ev = dto.ToEvent(out var error);
                    if (ev is null)
                    {
                        await WriteAsync(res, 400, new { error });
                        break;
                    }
                    await _ui.InvokeAsync(() => _resolver.Submit(ev));
                    await WriteAsync(res, 202, new { ok = true });
                    break;
                }
                case ("POST", "/dismiss"):
                {
                    var d = req.HasEntityBody
                        ? await JsonSerializer.DeserializeAsync<DismissDto>(req.InputStream, Json)
                        : null;
                    await _ui.InvokeAsync(() => _resolver.Dismiss(d?.Source, d?.Id));
                    await WriteAsync(res, 200, new { ok = true });
                    break;
                }
                case ("GET", "/result"):
                {
                    var source = req.QueryString["source"];
                    if (string.IsNullOrWhiteSpace(source))
                    {
                        await WriteAsync(res, 400, new { error = "source fehlt" });
                        break;
                    }
                    var id = req.QueryString["id"];
                    var action = await _ui.InvokeAsync(() => _resolver.TakeResult(source, id));
                    if (action is null) await WriteAsync(res, 204, null);
                    else await WriteAsync(res, 200, new { action });
                    break;
                }
                default:
                    await WriteAsync(res, 404, new { error = "not found" });
                    break;
            }
        }
        catch (JsonException ex)
        {
            Log.Error("Ungültiges JSON", ex);
            await TryWriteAsync(res, 400, new { error = "invalid json" });
        }
        catch (Exception ex)
        {
            Log.Error("Request-Fehler", ex);
            await TryWriteAsync(res, 500, new { error = "internal" });
        }
        finally
        {
            try { res.Close(); } catch { /* Client weg */ }
        }
    }

    private bool IsAuthorized(HttpListenerRequest req)
    {
        var header = req.Headers["X-Notch-Token"];
        return !string.IsNullOrEmpty(header)
               && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), _token);
    }

    private static async Task WriteAsync(HttpListenerResponse res, int status, object? body)
    {
        res.StatusCode = status;
        if (body is null)
        {
            res.ContentLength64 = 0;
            return;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    private static async Task TryWriteAsync(HttpListenerResponse res, int status, object body)
    {
        try { await WriteAsync(res, status, body); } catch { /* Antwort evtl. schon gesendet */ }
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch { /* beim Beenden egal */ }
    }
}
