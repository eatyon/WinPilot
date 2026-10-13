using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WinPilot.Core;

public sealed record WinPilotSettings
{
    private static readonly object configurationGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static WinPilotSettings Default { get; } = new();

    [JsonPropertyName("port")]
    public int Port { get; init; } = 9123;

    [JsonPropertyName("lanAccessEnabled")]
    public bool LanAccessEnabled { get; init; }

    [JsonPropertyName("startupMode")]
    public StartupMode StartupMode { get; init; } = StartupMode.Disabled;

    [JsonPropertyName("unauthenticatedWebPageEnabled")]
    public bool UnauthenticatedWebPageEnabled { get; init; }

    [JsonPropertyName("notifications")]
    public NotificationSettings Notifications { get; init; } = new();

    [JsonPropertyName("commandBots")]
    public CommandBotSettings CommandBots { get; init; } = new();

    [JsonPropertyName("safeModeEnabled")]
    public bool SafeModeEnabled { get; init; }

    [JsonPropertyName("securityNotifications")]
    public SecurityNotificationSettings SecurityNotifications { get; init; } = new();

    [JsonPropertyName("updates")]
    public UpdateSettings Updates { get; init; } = new();

    public static WinPilotSettings LoadAndInitialize(string configurationFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationFile);
        lock (configurationGate) return LoadCore(configurationFile);
    }

    public static WinPilotSettings Update(string configurationFile, Func<WinPilotSettings, WinPilotSettings> update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationFile);
        ArgumentNullException.ThrowIfNull(update);
        lock (configurationGate)
        {
            var next = NormalizeAndValidate(update(LoadCore(configurationFile)));
            WriteConfiguration(configurationFile, next);
            return next;
        }
    }

    private static WinPilotSettings LoadCore(string configurationFile)
    {
        if (!File.Exists(configurationFile))
        {
            var defaults = new WinPilotSettings();
            WriteConfiguration(configurationFile, defaults);
            return defaults;
        }
        return NormalizeAndValidate(ReadConfiguration(configurationFile).Deserialize<WinPilotSettings>(JsonOptions));
    }

    private static JsonObject ReadConfiguration(string configurationFile)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(configurationFile)) as JsonObject
                ?? throw new InvalidDataException("配置文件根节点必须是 JSON 对象");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("配置文件不是有效 JSON", exception);
        }
    }

    private static WinPilotSettings NormalizeAndValidate(WinPilotSettings? settings)
    {
        if (settings is null || settings.Port is < 1 or > 65535 || !Enum.IsDefined(settings.StartupMode)
            || settings.Updates is { IsValid: false })
            throw new InvalidDataException("配置内容不符合当前 WinPilot 版本要求");
        return settings with
        {
            Notifications = settings.Notifications ?? new(),
            CommandBots = settings.CommandBots ?? new(),
            SecurityNotifications = settings.SecurityNotifications ?? new(),
            Updates = settings.Updates ?? new()
        };
    }

    private static void WriteConfiguration(string configurationFile, WinPilotSettings settings)
    {
        var configuration = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
        if (File.Exists(configurationFile))
            foreach (var property in ReadConfiguration(configurationFile))
                if (!configuration.Any(item => item.Key.Equals(property.Key, StringComparison.OrdinalIgnoreCase)))
                    configuration.Add(property.Key, property.Value?.DeepClone());
        var directory = Path.GetDirectoryName(configurationFile)
            ?? throw new InvalidOperationException("配置文件路径必须包含目录");
        Directory.CreateDirectory(directory);
        var temporaryFile = configurationFile + ".tmp";
        try
        {
            File.WriteAllText(temporaryFile, configuration.ToJsonString(JsonOptions));
            File.Move(temporaryFile, configurationFile, overwrite: true);
        }
        finally { if (File.Exists(temporaryFile)) File.Delete(temporaryFile); }
    }
}
