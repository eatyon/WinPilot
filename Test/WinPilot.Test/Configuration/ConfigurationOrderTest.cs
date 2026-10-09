using System.Text.Json.Nodes;
using WinPilot.Core;

namespace WinPilot.Test;

public sealed class ConfigurationOrderTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurationWritesPropertiesInConsistentOrder(bool initialize)
    {
        var directory = Path.Combine(Path.GetTempPath(), "winpilot-configuration-order-tests", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "config.json");
        Directory.CreateDirectory(directory);
        try
        {
            var source = JsonNode.Parse("""
                {
                  "extra": { "value": 42 },
                  "securityNotifications": {},
                  "safeModeEnabled": true,
                  "commandBots": {
                    "QQBotEnabled": false,
                    "QQBotPrefix": "qq",
                    "OneBotEnabled": false,
                    "OneBotPrefix": "one",
                    "OneBotWebSocketUrl": "ws://localhost:3001",
                    "OneBotToken": "test-token",
                    "FeishuEnabled": false,
                    "FeishuPrefix": "feishu",
                    "TelegramEnabled": false,
                    "TelegramPrefix": "tg"
                  },
                  "notifications": {},
                  "unauthenticatedWebPageEnabled": false,
                  "startupMode": "Disabled",
                  "lanAccessEnabled": false,
                  "port": 9123
                }
                """)!.AsObject();
            if (initialize) source.Remove("startupMode");
            File.WriteAllText(file, source.ToJsonString());
            var settings = WinPilotSettings.LoadAndInitialize(file);
            if (initialize) Assert.Equal(source.ToJsonString(), File.ReadAllText(file));
            settings.Save(file);
            var saved = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var commands = saved["commandBots"]!.AsObject();
            Assert.True(saved["safeModeEnabled"]!.GetValue<bool>());
            Assert.Equal("test-token", commands["OneBotToken"]!.GetValue<string>());
            Assert.Equal("qq", commands["QQBotPrefix"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(source["extra"], saved["extra"]));
            var rootOrder = saved.Select(property => property.Key).ToArray();
            var commandOrder = commands.Select(property => property.Key).ToArray();
            var reordered = new JsonObject(saved.Reverse()
                .Select(property => new KeyValuePair<string, JsonNode?>(property.Key, property.Value?.DeepClone())));
            reordered["commandBots"] = new JsonObject(commands.Reverse()
                .Select(property => new KeyValuePair<string, JsonNode?>(property.Key, property.Value?.DeepClone())));
            File.WriteAllText(file, reordered.ToJsonString());
            WinPilotSettings.LoadAndInitialize(file).Save(file);
            var normalized = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            Assert.Equal(rootOrder, normalized.Select(property => property.Key));
            Assert.Equal(commandOrder, normalized["commandBots"]!.AsObject().Select(property => property.Key));
            var firstSave = File.ReadAllText(file);
            settings.Save(file);
            Assert.Equal(firstSave, File.ReadAllText(file));
        }
        finally { Directory.Delete(directory, true); }
    }
}
