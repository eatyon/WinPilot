using System.Text.RegularExpressions;
using WinPilot.Core.Notifications;
using WinPilot.Core.Tasks;
using WinPilot.Features.Desktop;
using WinPilot.Features.Notifications;
using WinPilot.Features.Plugins;
using WinPilot.PluginContract;

namespace WinPilot.Features.Tasks;

/// <summary>
/// 负责任务通知的内容校验、变量解析、图片准备和投递。
/// </summary>
internal sealed class TaskNotificationSender(
    DesktopScreenshotService screenshots,
    INotificationService notifications,
    PluginTaskExtensionRegistry extensions,
    TaskVariableCatalog variableCatalog,
    ILogger logger)
{
    /// <summary>
    /// 按需解析插件变量、准备正文和单张图片，再通过指定渠道投递。
    /// </summary>
    internal async Task SendAsync(
        TaskAction action,
        PluginTaskRunContext context,
        PluginImageContent? pluginImage = null)
    {
        var channel = action.NotificationChannel;
        NotificationChannelCatalog.RequireChannel(channel);
        var (contentTemplate, directImagePath) = ExtractDirectImagePath(action.NotificationContent);
        var contentNames = VariableResolver.ExtractNames(contentTemplate);
        var contentImageNames = ValidateImageVariables(contentTemplate, variableCatalog);
        if ((directImagePath is not null && contentImageNames.Length > 0)
            || (pluginImage is not null && (directImagePath is not null || contentImageNames.Length > 0)))
        {
            throw new InvalidOperationException("图片仅允许一个");
        }
        var requested = contentNames
            .Where(name =>
                !name.Equals("$time", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("&screenshot", StringComparison.OrdinalIgnoreCase)
                && !context.Variables.ContainsKey(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var variables = new Dictionary<string, object?>(context.Variables, StringComparer.OrdinalIgnoreCase);
        // 只向插件请求正文实际引用且上下文尚未提供的变量，避免无关变量产生额外开销。
        if (requested.Length > 0)
        {
            var resolved = await extensions.ResolveVariablesAsync(requested, context, context.CancellationToken);
            foreach (var (name, value) in resolved)
            {
                variables[name] = value;
            }
        }

        // 已注册文字变量无值时清空占位符，未知变量仍保留原文。
        foreach (var variable in variableCatalog.GetAll())
        {
            if (variable.Type == PluginVariableType.Text
                && contentNames.Contains(variable.Name, StringComparer.OrdinalIgnoreCase)
                && (!variables.TryGetValue(variable.Name, out var value) || value is null))
            {
                variables[variable.Name] = string.Empty;
            }
        }

        var resolvedContext = context with
        {
            Variables = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(variables)
        };
        // 截图在发送阶段按需采集，不在任务保存或变量列表查询时生成。
        if (contentImageNames.Contains("&screenshot", StringComparer.OrdinalIgnoreCase))
        {
            var screenshot = screenshots.Capture();
            variables["&screenshot"] = new PluginImageContent(screenshot.Content, screenshot.ContentType);
        }

        var textTemplate = contentImageNames.Length == 0
            ? contentTemplate
            : VariableResolver.Remove(contentTemplate, contentImageNames[0]);
        var content = ResolveNotificationContent(textTemplate, resolvedContext);
        if (directImagePath is not null)
        {
            ValidateImagePath(directImagePath);
        }
        var image = pluginImage ?? ResolveNotificationImage(contentImageNames.FirstOrDefault(), resolvedContext);
        if (pluginImage is not null)
        {
            PluginNotificationImageValidator.ValidateImageSize(pluginImage);
        }
        await NotificationDelivery.SendAsync(
            notifications,
            new NotificationMessage(content, directImagePath),
            channel,
            context.CancellationToken,
            imageContent: image?.Content,
            imageContentType: image?.ContentType,
            logger: logger);
    }

    private static PluginImageContent? ResolveNotificationImage(string? name, PluginTaskRunContext context)
    {
        if (name is null)
        {
            return null;
        }
        if (context.Variables.TryGetValue(name, out var value) && value is PluginImageContent image)
        {
            return image.Content.Length == 0 ? null : image;
        }
        throw new InvalidOperationException($"图片变量 {name} 不可用");
    }

    /// <summary>
    /// 仅识别带成对双引号的本地图片变量并从正文移除；不完整的写法按普通文字保留。
    /// </summary>
    internal static (string? Content, string? ImagePath) ExtractDirectImagePath(string? template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return (template, null);
        }
        string? imagePath = null;
        var content = Regex.Replace(template, "&image=\"(?<path>[^\"\\r\\n]*)\"", image =>
        {
            if (imagePath is not null)
            {
                throw new InvalidOperationException("图片仅允许一个");
            }
            var value = image.Groups["path"].Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("图片路径不能为空");
            }
            imagePath = value;
            return string.Empty;
        }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

        return (content, imagePath);
    }

    private static void ValidateImagePath(string path)
    {
        ValidateImagePathFormat(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("通知图片不存在", path);
        }
        if (new FileInfo(path).Length > NotificationDelivery.MaxImageSizeBytes)
        {
            throw new InvalidOperationException("通知图片不能超过 10 MB");
        }
    }

    private static void ValidateImagePathFormat(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("图片路径必须是绝对路径");
        }
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("通知图片仅支持 PNG 或 JPEG");
        }
    }

    /// <summary>
    /// 使用发送时刻填充 $time，保留其他触发上下文变量。
    /// </summary>
    internal static string ResolveNotificationContent(string? template, PluginTaskRunContext context)
    {
        var variables = new Dictionary<string, object?>(context.Variables, StringComparer.Ordinal)
        {
            ["$time"] = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}]"
        };
        var notificationContext = context with
        {
            Variables = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(variables)
        };
        return VariableResolver.Resolve(template, notificationContext);
    }

    internal void ValidateActions(WinPilotTask task)
    {
        for (var index = 0; index < task.Actions.Count; index++)
        {
            var action = task.Actions[index];
            if (action.Type != TaskActionType.SendNotification)
            {
                continue;
            }
            try
            {
                ValidateNotificationImages(action.NotificationContent, variableCatalog);
            }
            catch (InvalidOperationException exception)
            {
                throw new TaskValidationException($"actions[{index}].notificationContent", exception.Message, exception);
            }
        }
    }

    /// <summary>
    /// 保存时检查图片数量、完整语法中的空路径与路径格式；文件存在性和大小留到发送时检查。
    /// </summary>
    internal static void ValidateNotificationImages(string? content, TaskVariableCatalog variableCatalog)
    {
        var (text, imagePath) = ExtractDirectImagePath(content);
        var images = ValidateImageVariables(text, variableCatalog);
        if (imagePath is not null && images.Length > 0)
        {
            throw new InvalidOperationException("图片仅允许一个");
        }
        if (imagePath is not null)
        {
            ValidateImagePathFormat(imagePath);
        }
    }

    internal static string[] ValidateImageVariables(string? content, TaskVariableCatalog variableCatalog)
    {
        var images = VariableResolver
            .ExtractOccurrences(content)
            .Select(variableCatalog.MatchRegisteredImage)
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();
        if (images.Length > 1)
        {
            throw new InvalidOperationException("图片仅允许一个");
        }
        return images;
    }
}
