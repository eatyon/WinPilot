using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class StartupConfigurationTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "winpilot-startup-config", Guid.NewGuid().ToString("N"));
    private string PathToConfig => Path.Combine(root, "config.json");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedApplyOrSaveRestoresOriginalStartupMode(bool failApply)
    {
        WinPilotSettings.LoadAndInitialize(PathToConfig);
        var original = File.ReadAllText(PathToConfig);
        if (!failApply) Directory.CreateDirectory(PathToConfig + ".tmp");
        var applied = new List<StartupMode>();
        void Apply(StartupMode mode)
        {
            applied.Add(mode);
            if (failApply && mode == StartupMode.Enabled) throw new InvalidOperationException("测试失败");
        }
        var exception = Record.Exception(() => SettingsEndpointRouteBuilderExtensions.UpdateStartupSettings(
            PathToConfig, current => current with { StartupMode = StartupMode.Enabled }, Apply,
            NullLogger.Instance));
        Assert.NotNull(exception);
        Assert.Equal(new[] { StartupMode.Enabled, StartupMode.Disabled }, applied);
        Assert.Equal(original, File.ReadAllText(PathToConfig));
    }

    [Fact]
    public void InvalidModeNeverChangesStartupTask()
    {
        WinPilotSettings.LoadAndInitialize(PathToConfig);
        var original = File.ReadAllText(PathToConfig);
        var called = false;
        Assert.Throws<InvalidDataException>(() => SettingsEndpointRouteBuilderExtensions.UpdateStartupSettings(
            PathToConfig, current => current with { StartupMode = (StartupMode)999 }, _ => called = true,
            NullLogger.Instance));
        Assert.False(called);
        Assert.Equal(original, File.ReadAllText(PathToConfig));
    }

    [Fact]
    public void SuccessfulUpdatePreservesOtherSettings()
    {
        WinPilotSettings.Update(PathToConfig, current => current with { SafeModeEnabled = true, Port = 9456 });
        var applied = new List<StartupMode>();
        var saved = SettingsEndpointRouteBuilderExtensions.UpdateStartupSettings(PathToConfig,
            current => current with { StartupMode = StartupMode.Delayed }, applied.Add, NullLogger.Instance);
        Assert.Equal(new[] { StartupMode.Delayed }, applied);
        Assert.Equal(StartupMode.Delayed, WinPilotSettings.LoadAndInitialize(PathToConfig).StartupMode);
        Assert.True(saved.SafeModeEnabled);
        Assert.Equal(9456, saved.Port);
    }

    [Fact]
    public void FailedRollbackIsLoggedAndOriginalErrorIsPreserved()
    {
        WinPilotSettings.LoadAndInitialize(PathToConfig);
        var logger = new CommandBotTestObserver<StartupConfigurationTest>();
        var originalError = new InvalidOperationException("原始错误");
        Assert.Same(originalError, Record.Exception(() => SettingsEndpointRouteBuilderExtensions.UpdateStartupSettings(
            PathToConfig, current => current with { StartupMode = StartupMode.Enabled }, mode =>
            {
                if (mode == StartupMode.Enabled) throw originalError;
                throw new InvalidOperationException("恢复失败");
            }, logger)));
        Assert.Contains(logger.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
