using WinPilot.Features.Plugins;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed class PluginContractTest
{
    [Fact]
    public void HostAssemblyDoesNotImplementPluginContract()
    {
        var types = typeof(PluginManager).Assembly.GetTypes();
        Assert.DoesNotContain(types, type => typeof(ICSharpPlugin).IsAssignableFrom(type));
    }

    [Fact]
    public void PluginHostContext_ExposesRequiredSecurityContext()
    {
        var security = new FixedPluginSecurity(false);
        var context = new PluginHostContext(null, null, security);

        Assert.Same(security, context.Security);
    }

    private sealed class FixedPluginSecurity(bool enabled) : IPluginHostSecurityApi
    {
        public bool SafeModeEnabled { get; } = enabled;
    }
}
