using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace CastBox.ScreenShare;

/// <summary>Sends one screen, as an MPEG-TS stream, to <paramref name="output"/> until the viewer leaves or <paramref name="ct"/> is cancelled.</summary>
public interface IScreenStreamer
{
    Task StreamAsync(int screen, bool withSound, Stream output, CancellationToken ct);
}

/// <summary>The HTTP side of screen sharing (see docs/api-contracts.md): list screens, ask to watch one, receive one the PC sends, and stream it.</summary>
public static class ScreenEndpoints
{
    /// <param name="streamer">null when FFmpeg isn't available: screens are listed as unavailable and nothing can be streamed.</param>
    /// <param name="lanIp">The PC's address as the TV should reach it.</param>
    /// <param name="onWatching">Called when a TV starts watching (the window can tell the person).</param>
    public static void Map(IEndpointRouteBuilder app, ScreenShareService service, IScreenStreamer? streamer, Func<string> lanIp, int port,
        Action<string> log, Action<ShareSession, string>? onWatching = null, int maxStreams = 3, Func<string, string, string?>? hlsFile = null)
    {
        var slots = new SemaphoreSlim(maxStreams);
        string StreamUrl(int screen, string token) => $"http://{lanIp()}:{port}/api/screens/stream?screen={screen}&token={token}";
        object RequestJson(ShareRequest r) => new
        {
            requestId = r.Id, status = r.Status.ToString().ToLowerInvariant(),
            streamUrl = r.Status == ShareStatus.Approved && r.Token is not null ? StreamUrl(r.Screen, r.Token) : null,
        };

        app.MapGet("/api/screens", (string? tvId, string? tvName, HttpContext ctx) =>
        {
            if (!string.IsNullOrWhiteSpace(tvId)) service.Touch(tvId, tvName ?? "TV", ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString());
            return Results.Ok(new
            {
                available = streamer is not null, sound = true, pcName = Environment.MachineName,
                screens = service.Screens.Select(s => new { index = s.Index, name = s.Name, width = s.Width, height = s.Height, primary = s.Primary }).ToList(),
            });
        });

        app.MapPost("/api/screens/request", async (HttpContext ctx) =>
        {
            if (streamer is null) return Results.Json(new { error = "ffmpeg_missing" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            JsonNode? body;
            try
            {
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                var text = await reader.ReadToEndAsync();
                if (text.Length > 2048) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                body = JsonNode.Parse(text);
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid_json" }); }
            if (body is not JsonObject o) return Results.BadRequest(new { error = "invalid_json" });
            var tvId = (string?)o["tvId"]; var tvName = (string?)o["tvName"]; int? screen = (int?)o["screen"];
            if (string.IsNullOrWhiteSpace(tvId) || tvId.Length > 100 || screen is null) return Results.BadRequest(new { error = "invalid_request" });
            var request = service.Request(tvId, tvName ?? "TV", screen.Value);
            return request is null ? Results.NotFound(new { error = "unknown_screen" }) : Results.Json(RequestJson(request), statusCode: StatusCodes.Status202Accepted);
        });

        app.MapGet("/api/screens/request/{id}", (string id) =>
            service.Poll(id) is { } r ? Results.Ok(RequestJson(r)) : Results.NotFound(new { error = "unknown_request" }));

        // A TV with the app open asks here for a screen the PC wants to send it (the "Share screen" button in the server window).
        app.MapGet("/api/screens/inbox", async (string tvId, string? tvName, int? wait, HttpContext ctx) =>
        {
            var until = DateTime.UtcNow.AddSeconds(Math.Clamp(wait ?? 0, 0, 30));
            var lastTouch = DateTime.MinValue;
            try
            {
                do
                {
                    if (DateTime.UtcNow - lastTouch > TimeSpan.FromSeconds(5)) { service.Touch(tvId, tvName ?? "TV", ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString()); lastTouch = DateTime.UtcNow; }
                    if (service.TakePush(tvId) is { } offer)
                        return Results.Ok(new { screen = offer.Screen, name = service.GetScreen(offer.Screen)?.Name, pcName = Environment.MachineName, streamUrl = StreamUrl(offer.Screen, offer.Token) });
                    await Task.Delay(100, ctx.RequestAborted);
                } while (DateTime.UtcNow < until);
            }
            catch (OperationCanceledException) { /* the TV went away */ }
            return Results.NoContent();
        });

        // The live HLS playlist and its pieces for a Chromecast the PC is casting to (the random session id in the path is the secret).
        app.MapGet("/api/screens/hls/{session}/{file}", (string session, string file, HttpContext ctx) =>
        {
            var path = hlsFile?.Invoke(session, file);
            if (path is null) return Results.NotFound();
            try
            {
                ctx.Response.Headers.CacheControl = "no-store";
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Results.File(stream, file.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t");
            }
            catch (IOException) { return Results.NotFound(); }
        });

        app.MapGet("/api/screens/stream", async (HttpContext ctx, int screen, string? token) =>
        {
            if (streamer is null) { ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
            var session = service.Open(token, screen);
            if (session is null) { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            if (!slots.Wait(0)) { service.Close(session); ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
            try
            {
                ctx.Response.ContentType = "video/mp2t";
                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                var name = service.GetScreen(screen)?.Name ?? "a screen";
                log($"{session.TvName} is watching {name}.");
                onWatching?.Invoke(session, name);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, session.Ended);
                await streamer.StreamAsync(screen, true, ctx.Response.Body, linked.Token);
            }
            finally { slots.Release(); service.Close(session); log($"{session.TvName} stopped watching."); }
        });
    }
}
