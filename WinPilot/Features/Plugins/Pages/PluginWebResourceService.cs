using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;
using WinPilot.Core.Plugins;

namespace WinPilot.Features.Plugins;

/// <summary>
/// 解析已启用插件的 Web 资源，并生成用于资源缓存更新的版本标识。
/// </summary>
public sealed class PluginWebResourceService(PluginManager manager)
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html",
        ".htm",
        ".js",
        ".mjs",
        ".css",
        ".json",
        ".svg",
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp",
        ".ico",
        ".woff",
        ".woff2",
        ".ttf"
};
    private readonly FileExtensionContentTypeProvider contentTypes = new();
    private readonly object versionGate = new();
    private readonly ConditionalWeakTable<PluginManifest, WebVersion> versions = new();

    public string GetVersion(string pluginId, bool refresh = true)
    {
        var plugin = manager.GetPlugin(pluginId);
        var manifest = plugin.Manifest ?? throw new FileNotFoundException("插件声明不存在");
        lock (versionGate)
        {
            if (!refresh && versions.TryGetValue(manifest, out var cached))
            {
                return cached.Value;
            }
            var version = CalculateVersion(plugin.Directory);
            versions.Remove(manifest);
            versions.Add(manifest, new(version));
            return version;
        }
    }

    private static string CalculateVersion(string directory)
    {
        // 版本依据相对路径、长度和修改时间生成，不是文件内容的完整性校验。
        var webRoot = Path.Combine(directory, "web");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (Directory.Exists(webRoot) && !HasReparsePoint(directory, webRoot))
        {
            foreach (var file in Directory.EnumerateFiles(webRoot, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            })
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                var identity = $"{Path.GetRelativePath(webRoot, file).Replace('\\', '/')}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}\n";
                hash.AppendData(Encoding.UTF8.GetBytes(identity));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset().AsSpan(0, 16)).ToLowerInvariant();
    }

    /// <summary>
    /// 仅返回插件 web 目录内允许的文件类型，拒绝绝对路径、越界路径及经过重解析点的路径。
    /// </summary>
    public (string Path, string ContentType)? Resolve(string pluginId, string? resourcePath)
    {
        var plugin = manager.GetPlugin(pluginId);
        if (!plugin.Enabled || string.IsNullOrWhiteSpace(plugin.Manifest?.FeaturesUi))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            return null;
        }
        var webRoot = Path.GetFullPath(Path.Combine(plugin.Directory, "web"));
        var decoded = Uri.UnescapeDataString(resourcePath).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(decoded))
        {
            return null;
        }
        var fullPath = Path.GetFullPath(Path.Combine(webRoot, decoded));
        var root = webRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            return null;
        }
        if (HasReparsePoint(plugin.Directory, fullPath))
        {
            return null;
        }
        var extension = Path.GetExtension(fullPath);
        if (!AllowedExtensions.Contains(extension))
        {
            return null;
        }
        return (fullPath, contentTypes.TryGetContentType(fullPath, out var type) ? type : "application/octet-stream");
    }

    private static bool HasReparsePoint(string directory, string path)
    {
        // 逐级检查到插件根目录，防止符号链接或目录联接把资源访问引向目录外。
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private sealed record WebVersion(string Value);
}
