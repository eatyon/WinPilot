using WinPilot.Features.Plugins;
using System.Buffers;
using System.Threading.Channels;

namespace WinPilot.Web;

public static class PluginEndpointRouteBuilderExtensions
{
    private const int MaxPageMessageBytes = 64 * 1024;
    private const int MaxPageUploadBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapPluginEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/plugins", (PluginManager plugins) => Results.Ok(plugins.GetPlugins()));
        endpoints.MapGet("/api/plugins/{id}", async (string id, bool? waitForLifecycle, PluginManager plugins, CancellationToken cancellationToken) =>
        {
            if (waitForLifecycle != true) return Execute(() => Results.Ok(plugins.GetPlugin(id)));
            try { return Results.Ok(await plugins.GetPluginAfterLifecycleAsync(id, cancellationToken)); }
            catch (FileNotFoundException exception) { return Results.NotFound(new { detail = exception.Message }); }
        });
        endpoints.MapMethods("/api/plugins/{id}/readme", ["HEAD"], (string id, PluginManager plugins) => Execute(() => plugins.HasReadme(id) ? Results.Ok() : Results.NotFound()));
        endpoints.MapGet("/api/plugins/{id}/readme", (string id, PluginManager plugins) => Execute(() => Results.Text(plugins.ReadReadme(id), "text/plain; charset=utf-8")));
        endpoints.MapPost("/api/plugins/{id}/enable", (string id, PluginManager plugins) => Execute(() => { plugins.Enable(id); return Results.Ok(plugins.GetPlugin(id)); }));
        endpoints.MapPost("/api/plugins/{id}/disable", (string id, PluginManager plugins) => Execute(() => { plugins.Disable(id); return Results.Ok(plugins.GetPlugin(id)); }));
        endpoints.MapDelete("/api/plugins/{id}", (string id, bool deleteData, PluginManager plugins) => Execute(() => { plugins.Delete(id, deleteData); return Results.NoContent(); }));
        endpoints.MapPost("/api/plugins/scan", (PluginManager plugins) => { plugins.Reload(); return Results.Ok(plugins.GetPlugins()); });
        endpoints.MapGet("/api/plugins/{id}/configuration", (string id, PluginManager plugins, HttpContext context) => Execute(() => ReadConfiguration(id, plugins, context, false)));
        endpoints.MapPut("/api/plugins/{id}/configuration", (string id, System.Text.Json.JsonElement content, PluginManager plugins) => Execute(() => { plugins.WriteStorageAndRestart(id, content.GetRawText()); return Results.Ok(content); }));
        endpoints.MapGet("/api/plugins/{id}/status", (string id, PluginManager plugins) => Execute(() => Results.Content(plugins.GetStatus(id) ?? "{}", "application/json")));
        endpoints.MapGet("/api/plugins/{id}/settings/display/status", (string id, PluginManager plugins) => Execute(() => Results.Ok(plugins.GetStatusDisplaySettings(id))));
        endpoints.MapPut("/api/plugins/{id}/settings/display/status", (string id, PluginManager.PluginStatusDisplaySettings settings, PluginManager plugins) => Execute(() => Results.Ok(plugins.WriteStatusDisplaySettings(id, settings))));
        endpoints.MapGet("/api/plugins/{id}/settings/display/features", (string id, PluginManager plugins) => Execute(() => Results.Ok(plugins.GetFeatureDisplaySettings(id))));
        endpoints.MapPut("/api/plugins/{id}/settings/display/features", (string id, PluginManager.PluginFeatureDisplaySettings settings, PluginManager plugins) => Execute(() => Results.Ok(plugins.WriteFeatureDisplaySettings(id, settings))));
        endpoints.MapGet("/api/plugins/{id}/settings/display/configuration", (string id, PluginManager plugins) => Execute(() => Results.Ok(plugins.GetSettingsDisplaySettings(id))));
        endpoints.MapPut("/api/plugins/{id}/settings/display/configuration", (string id, PluginManager.PluginSettingsDisplaySettings settings, PluginManager plugins) => Execute(() => Results.Ok(plugins.WriteSettingsDisplaySettings(id, settings))));
        endpoints.MapGet("/api/plugins/{id}/configuration/schema", (string id, PluginManager plugins, HttpContext context) => Execute(() => ReadConfiguration(id, plugins, context, true)));
        endpoints.MapGet("/api/plugins/options/settings/{source}", (string source) => Execute(() => Results.Ok(PluginSettingOptionSources.GetOptions(source))));
        endpoints.MapGet("/api/plugins/{id}/features", (string id, PluginManager plugins) => Execute(() => Results.Content(plugins.ReadFeaturesDefinition(id), "application/json")));
        endpoints.MapPost("/api/plugins/{id}/features/{featureId}/execute", (string id, string featureId, PluginManager plugins) => Execute(() => Results.Ok(plugins.ExecuteFeature(id, featureId))));
        endpoints.MapPost("/api/plugins/{id}/features/{featureId}/page-sessions", (string id, string featureId, PluginManager plugins, PluginWebResourceService resources) => Execute(() =>
        {
            var version = resources.GetVersion(id, refresh: true);
            var session = plugins.OpenPageFeatureSession(id, featureId);
            return Results.Ok(new { session.Id, session.PluginName, session.Feature, WebVersion = version });
        }));
        endpoints.MapPost("/api/plugin-page-sessions/{sessionId}/messages", async (string sessionId, HttpRequest request, PluginManager plugins, HttpContext context) =>
        {
            if (request.ContentLength > MaxPageMessageBytes) return PageMessageTooLarge();
            try
            {
                var message = await ReadPageMessageAsync(request, context.RequestAborted);
                if (message is null) return PageMessageTooLarge();
                var response = await plugins.SendPageMessageAsync(sessionId, message, context.RequestAborted);
                return Results.Content(response, "application/json");
            }
            catch (KeyNotFoundException exception) { return Results.NotFound(new { detail = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "插件页面消息失败", detail: exception.Message); }
        });
        endpoints.MapPost("/api/plugin-page-sessions/{sessionId}/uploads", async (string sessionId, HttpRequest request, PluginManager plugins, CancellationToken cancellationToken) =>
        {
            if (request.ContentType != "application/octet-stream") return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            var parameters = request.Query["parameters"].ToString();
            var command = request.Query["command"].ToString();
            if (string.IsNullOrWhiteSpace(command) || command.Length > 128 || parameters.Length > 4096) return Results.BadRequest(new { detail = "插件上传参数无效或过长" });
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(string.IsNullOrEmpty(parameters) ? "{}" : parameters);
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return Results.BadRequest(new { detail = "插件上传参数无效" });
                var response = await plugins.UploadPageAsync(sessionId, async token =>
                {
                    var content = await ReadPageBodyAsync(request, MaxPageUploadBytes, token);
                    if (content is null) throw new BadHttpRequestException("单次上传不能超过 1 MB，请分块上传", StatusCodes.Status413PayloadTooLarge);
                    if (content.Length == 0) throw new BadHttpRequestException("插件上传数据为空");
                    return new(command, document.RootElement.GetRawText(), content);
                }, cancellationToken);
                return Results.Content(response, "application/json");
            }
            catch (KeyNotFoundException exception) { return Results.NotFound(new { detail = exception.Message }); }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { detail = "插件上传参数无效" }); }
            catch (BadHttpRequestException exception) { return Results.Problem(statusCode: exception.StatusCode, title: "插件上传失败", detail: exception.Message); }
            catch (InvalidOperationException exception) { return Results.Problem(statusCode: 400, title: "插件上传失败", detail: exception.Message); }
        });
        endpoints.MapDelete("/api/plugin-page-sessions/{sessionId}", (string sessionId, PluginManager plugins) => Execute(() => { plugins.ClosePageSession(sessionId); return Results.NoContent(); }));
        endpoints.MapPost("/api/plugin-page-sessions/{sessionId}/heartbeat", (string sessionId, PluginManager plugins) => Execute(() => { plugins.TouchPageSession(sessionId); return Results.NoContent(); }));
        endpoints.MapGet("/api/plugins/{id}/events", StreamPluginStatusAsync);
        endpoints.MapPost("/api/plugins/{id}/web/log", (string id, PluginWebLog message, PluginManager plugins, ILoggerFactory loggerFactory) => Execute(() => { var plugin = plugins.GetPlugin(id); if (!plugin.Enabled) throw new InvalidOperationException("plugin_disabled"); var logger = loggerFactory.CreateLogger($"Plugin:{plugin.Manifest?.Name ?? plugin.Id}"); logger.Log(message.Level?.ToLowerInvariant() switch { "warn" => LogLevel.Warning, "error" => LogLevel.Error, _ => LogLevel.Information }, "[Plugin:{PluginId}] {Message}", plugin.Id, message.Message ?? string.Empty); return Results.Ok(new { accepted = true }); }));
        endpoints.MapGet("/plugin-assets/{id}/v/{version}/{**path}", (HttpContext context, string id, string version, string? path, PluginWebResourceService resources) =>
            ServePluginAsset(context, id, path, resources, version));
        endpoints.MapGet("/plugin-assets/{id}/{**path}", (HttpContext context, string id, string? path, PluginWebResourceService resources) =>
            ServePluginAsset(context, id, path, resources));
        return endpoints;
    }

    internal static async Task<IResult> StreamPluginStatusAsync(string id, HttpContext context, PluginManager plugins)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        IDisposable subscription;
        try { subscription = plugins.SubscribeState(id, json => channel.Writer.TryWrite(json)); }
        catch (FileNotFoundException) { return Results.NotFound(); }
        try
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            var current = plugins.GetStatus(id);
            if (current is not null) await context.Response.WriteAsync($"data: {current}\n\n", context.RequestAborted);
            await foreach (var json in channel.Reader.ReadAllAsync(context.RequestAborted))
                await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        finally { subscription.Dispose(); channel.Writer.TryComplete(); }
        return Results.Empty;
    }

    private static IResult ReadConfiguration(string id, PluginManager plugins, HttpContext context, bool readDefinition)
    {
        context.Response.Headers.CacheControl = "no-store";
        var storage = plugins.ReadStorage(id);
        if (!readDefinition) return Results.Content(storage, "application/json");
        var definition = plugins.ReadSettingsDefinition(id);
        return Results.Content(readDefinition
            ? PluginSettingOptionSources.WithSelectedOptions(definition, storage) : storage, "application/json");
    }

    private static IResult ServePluginAsset(HttpContext context, string id, string? path, PluginWebResourceService resources, string? version = null)
    {
        try
        {
            if (version is not null && !string.Equals(version, resources.GetVersion(id, refresh: false), StringComparison.Ordinal)) return Results.NotFound();
            var resolved = resources.Resolve(id, path);
            if (resolved is null) return Results.NotFound();
            var isPageOrData = resolved.Value.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                || resolved.Value.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
            context.Response.Headers.CacheControl = version is not null && !isPageOrData
                ? "private, max-age=31536000, immutable"
                : "no-store";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
            return Results.File(resolved.Value.Path, resolved.Value.ContentType);
        }
        catch (FileNotFoundException) { return Results.NotFound(); }
    }

    private static IResult PageMessageTooLarge() =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "插件页面消息过大", detail: "单次插件页面消息不能超过 64 KB");

    private static async Task<string?> ReadPageMessageAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var content = await ReadPageBodyAsync(request, MaxPageMessageBytes, cancellationToken);
        if (content is null) return null;
        using var stream = new MemoryStream(content);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task<byte[]?> ReadPageBodyAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit) return null;
        using var content = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
            {
                if (content.Length + read > limit) return null;
                content.Write(buffer, 0, read);
            }
            return content.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static IResult Execute(Func<IResult> action)
    {
        try { return action(); }
        catch (KeyNotFoundException exception) { return Results.NotFound(new { detail = exception.Message }); }
        catch (FileNotFoundException exception) { return Results.NotFound(new { detail = exception.Message }); }
        catch (InvalidOperationException exception) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "插件操作失败", detail: exception.Message); }
        catch (InvalidDataException exception) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "插件配置无效", detail: exception.Message); }
        catch (System.Text.Json.JsonException) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "插件配置无效", detail: "插件配置内容不符合当前版本要求"); }
    }

    private sealed record PluginWebLog(string? Level, string? Message);
}
