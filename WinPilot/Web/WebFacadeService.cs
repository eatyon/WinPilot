using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.StaticFiles;
using WinPilot.Core;

namespace WinPilot.Web;

public sealed class WebFacadeService(PortableDataPaths paths)
{
    public const string VisitorCookieName = "WinPilot.WebFacadeVisitor";
    public const string ManagementEntryCookieName = "WinPilot.ManagementEntry";
    private readonly ConcurrentDictionary<string, DateTimeOffset> visitors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> entries = new(StringComparer.Ordinal);
    private long nextCleanupUtcTicks;
    private readonly FileExtensionContentTypeProvider contentTypes = new();

    public bool IsEnabled() => WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile).UnauthenticatedWebPageEnabled;

    public void EnsureDefaultPage()
    {
        Directory.CreateDirectory(paths.WebDirectory);
        if (File.Exists(paths.WebIndexFile)) return;
        File.WriteAllText(paths.WebIndexFile, DefaultPage);
    }

    public async Task ServeIndexAsync(HttpContext context)
    {
        RemoveExpiredTokens();
        EnsureDefaultPage();
        var visitor = CreateToken();
        visitors[visitor] = DateTimeOffset.UtcNow.AddMinutes(10);
        context.Response.Cookies.Append(VisitorCookieName, visitor, CookieOptions(context, TimeSpan.FromMinutes(10)));
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.SendFileAsync(paths.WebIndexFile, context.RequestAborted);
    }

    public async Task<bool> TryServeResourceAsync(HttpContext context)
    {
        var relativePath = context.Request.Path.Value?.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relativePath) || string.Equals(relativePath, "index.html", StringComparison.OrdinalIgnoreCase)) return false;
        var webRoot = Path.GetFullPath(paths.WebDirectory);
        var candidate = Path.GetFullPath(Path.Combine(webRoot, relativePath));
        if (!candidate.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate)) return false;
        for (var current = candidate; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            if (string.Equals(current, paths.ApplicationDirectory, StringComparison.OrdinalIgnoreCase)) break;
        }
        contentTypes.TryGetContentType(candidate, out var contentType);
        var file = new FileInfo(candidate);
        context.Response.Headers.CacheControl = "private, no-cache";
        await Results.File(candidate, contentType ?? "application/octet-stream", lastModified: file.LastWriteTimeUtc,
            entityTag: new($"\"{file.LastWriteTimeUtc.Ticks:x}-{file.Length:x}\"", isWeak: true)).ExecuteAsync(context);
        return true;
    }

    public bool GrantManagementEntry(HttpContext context)
    {
        RemoveExpiredTokens();
        if (!TryConsume(visitors, context.Request.Cookies[VisitorCookieName])) return false;
        var entry = CreateToken();
        entries[entry] = DateTimeOffset.UtcNow.AddMinutes(5);
        context.Response.Cookies.Append(ManagementEntryCookieName, entry, CookieOptions(context, TimeSpan.FromMinutes(5)));
        context.Response.Cookies.Delete(VisitorCookieName);
        return true;
    }

    public bool HasManagementEntry(HttpContext context) => IsActive(entries, context.Request.Cookies[ManagementEntryCookieName]);

    public void ConsumeManagementEntry(HttpContext context)
    {
        if (context.Request.Cookies[ManagementEntryCookieName] is { } entry) entries.TryRemove(entry, out _);
        context.Response.Cookies.Delete(ManagementEntryCookieName);
    }

    private void RemoveExpiredTokens()
    {
        var now = DateTimeOffset.UtcNow;
        var previous = Volatile.Read(ref nextCleanupUtcTicks);
        if (now.UtcTicks < previous || Interlocked.CompareExchange(ref nextCleanupUtcTicks, now.AddMinutes(1).UtcTicks, previous) != previous) return;
        foreach (var tokens in new[] { visitors, entries })
            foreach (var item in tokens)
                if (item.Value <= now)
                    ((ICollection<KeyValuePair<string, DateTimeOffset>>)tokens).Remove(item);
    }

    private static CookieOptions CookieOptions(HttpContext context, TimeSpan maxAge) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = context.Request.IsHttps,
        Path = "/",
        MaxAge = maxAge
    };

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsActive(ConcurrentDictionary<string, DateTimeOffset> tokens, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !tokens.TryGetValue(token, out var expiresAt)) return false;
        if (expiresAt > DateTimeOffset.UtcNow) return true;
        tokens.TryRemove(token, out _);
        return false;
    }

    private static bool TryConsume(ConcurrentDictionary<string, DateTimeOffset> tokens, string? token)
    {
        if (!IsActive(tokens, token)) return false;
        return tokens.TryRemove(token!, out _);
    }

    private const string DefaultPage = """
<html>
<head>
<title>404 Not Found</title>
<link rel="icon" href="data:,">
</head>
<body>
  <center><h1>404 Not Found</h1></center>
  <hr><center>nginx/1.31.5</center>
  <script>
    let pressTimer;
    const enterManagement = async () => {
      const response = await fetch('/_manage', { method: 'POST', credentials: 'same-origin' });
      if (response.ok) location.assign('/login');
    };
    document.addEventListener('pointerdown', () => { pressTimer = setTimeout(enterManagement, 800); });
    ['pointerup', 'pointercancel', 'pointerleave'].forEach(event => document.addEventListener(event, () => clearTimeout(pressTimer)));
  </script>
</body>
</html>
""";
}
