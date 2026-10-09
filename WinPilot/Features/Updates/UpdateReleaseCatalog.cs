using System.Net.Http.Headers;
using System.Text.Json;
using WinPilot.Core;

namespace WinPilot.Features.Updates;

internal sealed class UpdateReleaseCatalog(HttpClient httpClient)
{
    internal async Task<UpdateRelease?> GetAsync(UpdateSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Channel == UpdateChannel.Stable)
        {
            using var document = await RequestAsync(settings.Source, "latest", cancellationToken);
            return Parse(document.RootElement, settings)
                ?? throw new InvalidOperationException("Latest Release 不是有效的正式版本");
        }

        UpdateRelease? highest = null;
        for (var page = 1; page <= 1000; page++)
        {
            var query = settings.Source == UpdateSource.GitHub ? $"?per_page=100&page={page}" : $"?page_size=100&page={page}";
            using var document = await RequestAsync(settings.Source, query, cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Release 列表无效");
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var release = Parse(item, settings);
                if (release is not null && (highest is null || release.Version.CompareTo(highest.Version) > 0)) highest = release;
            }
            if (document.RootElement.GetArrayLength() < 100) return highest;
        }
        throw new InvalidOperationException("Release 列表分页超出限制，未完成版本检查");
    }

    private async Task<JsonDocument> RequestAsync(UpdateSource source, string suffix, CancellationToken cancellationToken)
    {
        var endpoint = source == UpdateSource.GitHub
            ? "https://api.github.com/repos/eatyon/WinPilot/releases"
            : "https://cnb.cool/asdbr/WinPilot/-/releases";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + (suffix == "latest" ? "/latest" : suffix));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(source == UpdateSource.GitHub ? "application/vnd.github+json" : "application/vnd.cnb.api+json"));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static UpdateRelease? Parse(JsonElement root, UpdateSettings settings)
    {
        if (root.ValueKind != JsonValueKind.Object || IsTrue(root, "draft")) return null;
        var tag = GetString(root, "tag_name");
        if (!SemanticVersion.TryParse(tag, out var version)) return null;
        if (settings.Channel == UpdateChannel.Stable && (IsTrue(root, "prerelease") || version.IsPrerelease)) return null;
        var releaseUrl = settings.Source == UpdateSource.GitHub
            ? $"https://github.com/eatyon/WinPilot/releases/tag/{Uri.EscapeDataString(tag!)}"
            : $"https://cnb.cool/asdbr/WinPilot/-/releases/tag/{Uri.EscapeDataString(tag!)}";
        var packageName = $"WinPilot-{tag}-win-x64.zip";
        var package = FindPackage(root, packageName, settings.Source);
        return new(version, releaseUrl, package, GetString(root, "body") ?? "");
    }

    private static string? ReadSha256(JsonElement asset, UpdateSource source)
    {
        string? hash;
        if (source == UpdateSource.GitHub)
        {
            var digest = GetString(asset, "digest");
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal)) return null;
            hash = digest[7..];
        }
        else
        {
            if (!string.Equals(GetString(asset, "hash_algo"), "sha256", StringComparison.OrdinalIgnoreCase)) return null;
            hash = GetString(asset, "hash_value");
        }
        return hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit) ? hash.ToLowerInvariant() : null;
    }

    private static ReleaseAsset? FindPackage(JsonElement release, string name, UpdateSource source)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        JsonElement? match = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || GetString(asset, "name") != name) continue;
            if (match is not null) return null;
            match = asset;
        }
        if (match is not { } package) return null;
        var url = GetString(package, "browser_download_url") ?? "";
        try { CreateDownloadUri(source, url); }
        catch (InvalidOperationException) { return null; }
        return new(name, url, ReadSha256(package, source));
    }

    internal static Uri CreateDownloadUri(UpdateSource source, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("更新下载地址无效");
        var valid = source switch
        {
            UpdateSource.GitHub => uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/eatyon/WinPilot/releases/download/", StringComparison.OrdinalIgnoreCase),
            UpdateSource.Cnb => (uri.Host.Equals("cnb.cool", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("api.cnb.cool", StringComparison.OrdinalIgnoreCase))
                && uri.AbsolutePath.StartsWith("/asdbr/WinPilot/-/releases/download/", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (!valid) throw new InvalidOperationException("更新下载地址不属于所选渠道仓库");
        if (source == UpdateSource.Cnb && uri.Host.Equals("api.cnb.cool", StringComparison.OrdinalIgnoreCase))
            return new UriBuilder(uri) { Host = "cnb.cool" }.Uri;
        return uri;
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool IsTrue(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

internal sealed record ReleaseAsset(string Name, string Url, string? Sha256);
internal sealed record UpdateRelease(SemanticVersion Version, string ReleaseUrl, ReleaseAsset? Package, string ReleaseNotes)
{
    public bool CanInstall => Package?.Sha256 is not null;
}
