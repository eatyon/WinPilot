using System.Text.Json.Serialization;

namespace WinPilot.Core;

public enum UpdateSource { GitHub, Cnb }
public enum UpdateChannel { Stable, Preview }

public sealed record UpdateSettings
{
    [JsonPropertyName("source")]
    public UpdateSource Source { get; init; } = UpdateSource.GitHub;

    [JsonPropertyName("channel")]
    public UpdateChannel Channel { get; init; } = UpdateChannel.Stable;

    [JsonIgnore]
    public bool IsValid => Enum.IsDefined(Source) && Enum.IsDefined(Channel);
}
