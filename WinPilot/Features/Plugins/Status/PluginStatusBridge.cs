using WinPilot.PluginContract;

namespace WinPilot.Features.Plugins;

public sealed class PluginStatusBridge : IPluginStatus, IDisposable
{
    private readonly object gate = new();
    private readonly PluginStatusDelivery delivery = new();
    private string? statusJson;
    private int subscriberCount;

    public string? StatusJson => Volatile.Read(ref statusJson);

    public event Action<string>? StateChanged;
    public event Action<int>? SubscriberCountChanged;

    public void Publish(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var normalized = document.RootElement.GetRawText();
        PluginStatusDelivery.Delivery pending;
        lock (gate)
        {
            if (string.Equals(statusJson, normalized, StringComparison.Ordinal))
            {
                return;
            }
            Volatile.Write(ref statusJson, normalized);
            var callback = StateChanged;
            pending = delivery.Enqueue(_ => callback?.Invoke(normalized));
        }
        delivery.Deliver(pending);
    }

    public void SetStatusSubscriberCount(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        if (Interlocked.Exchange(ref subscriberCount, count) != count)
        {
            SubscriberCountChanged?.Invoke(count);
        }
    }

    public void Dispose()
    {
        StateChanged = null;
        SubscriberCountChanged = null;
    }
}
