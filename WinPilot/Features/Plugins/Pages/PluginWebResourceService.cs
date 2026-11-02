using Microsoft.AspNetCore.StaticFiles;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using WinPilot.Core.Plugins;

namespace WinPilot.Features.Plugins;

public sealed class PluginWebResourceService(PluginManager manager)
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".html", ".htm", ".js", ".mjs", ".css", ".json", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".woff", ".woff2", ".ttf" };
    private readonly FileExtensionContentTypeProvider contentTypes = new();
    private readonly object versionGate = new();
    private readonly ConditionalWeakTable<PluginManifest, WebVersion> versions = new();

    public string GetVersion(string pluginId, bool refresh = true)
    {
        var plugin = manager.GetPlugin(pluginId);
        var manifest = plugin.Manifest ?? throw new FileNotFoundException("插件声明不存在");
        lock (versionGate)
        {
            if (!refresh && versions.TryGetValue(manifest, out var cached)) return cached.Value;
            var version = CalculateVersion(plugin.Directory);
            versions.Remove(manifest);
            versions.Add(manifest, new(version));
            return version;
        }
    }

    private static string CalculateVersion(string directory)
    {
        var webRoot = Path.Combine(directory, "web");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (Directory.Exists(webRoot) && !HasReparsePoint(directory, webRoot))
        {
            foreach (var file in Directory.EnumerateFiles(webRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                var identity = $"{Path.GetRelativePath(webRoot, file).Replace('\\', '/')}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}\n";
                hash.AppendData(Encoding.UTF8.GetBytes(identity));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset().AsSpan(0, 16)).ToLowerInvariant();
    }

    public (string Path, string ContentType)? Resolve(string pluginId, string? resourcePath)
    {
        var plugin = manager.GetPlugin(pluginId);
        if (!plugin.Enabled || string.IsNullOrWhiteSpace(plugin.Manifest?.FeaturesUi)) return null;
        if (string.IsNullOrWhiteSpace(resourcePath)) return null;
        var webRoot = Path.GetFullPath(Path.Combine(plugin.Directory, "web"));
        var decoded = Uri.UnescapeDataString(resourcePath).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(decoded)) return null;
        var fullPath = Path.GetFullPath(Path.Combine(webRoot, decoded));
        var root = webRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return null;
        if (HasReparsePoint(plugin.Directory, fullPath)) return null;
        var extension = Path.GetExtension(fullPath);
        if (!AllowedExtensions.Contains(extension)) return null;
        return (fullPath, contentTypes.TryGetContentType(fullPath, out var type) ? type : "application/octet-stream");
    }

    private static bool HasReparsePoint(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private sealed record WebVersion(string Value);
}
