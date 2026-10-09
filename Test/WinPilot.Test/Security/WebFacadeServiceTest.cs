using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using WinPilot.Core;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class WebFacadeServiceTest
{
    [Fact]
    public void ExpiredUnusedTokensArePrunedWithoutRemovingActiveTokens()
    {
        var service = new WebFacadeService(PortableDataPaths.Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        foreach (var name in new[] { "visitors", "entries" })
        {
            var tokens = (ConcurrentDictionary<string, DateTimeOffset>)typeof(WebFacadeService)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            tokens["expired"] = DateTimeOffset.UtcNow.AddMinutes(-1);
            tokens["active"] = DateTimeOffset.UtcNow.AddMinutes(1);
        }
        Assert.False(service.GrantManagementEntry(new DefaultHttpContext()));
        foreach (var name in new[] { "visitors", "entries" })
        {
            var tokens = (ConcurrentDictionary<string, DateTimeOffset>)typeof(WebFacadeService)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            Assert.False(tokens.ContainsKey("expired"));
            Assert.True(tokens.ContainsKey("active"));
        }
    }
}
