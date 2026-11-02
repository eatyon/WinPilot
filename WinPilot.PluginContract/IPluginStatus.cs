namespace WinPilot.PluginContract;

/// <summary>
/// 插件状态发布接口；订阅数量变化可用于按需启停状态采集，发布内容为 JSON。
/// </summary>
public interface IPluginStatus : IDisposable
{
    string? StatusJson { get; }

    event Action<string>? StateChanged;
    event Action<int>? SubscriberCountChanged;

    void Publish(string json);
}
