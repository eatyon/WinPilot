using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WinPilot.Core;
using WinPilot.Features.Logs;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class LogEndpointTest : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "WinPilot.Log.Endpoint.Test", Guid.NewGuid().ToString("N"));
    private PortableDataPaths paths = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        paths = PortableDataPaths.Create(directory);
        Directory.CreateDirectory(paths.LogsDirectory);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new LogQueryService(paths));
        app = builder.Build();
        app.MapLogEndpoints();
        await app.StartAsync();
        client = app.GetTestClient();
    }

    [Theory]
    [InlineData("date=bad")]
    [InlineData("date=2026-10-04&level=bad")]
    [InlineData("date=2026-10-04&offset=-1")]
    [InlineData("date=2026-10-04&snapshot=-1")]
    public async Task InvalidQueriesReturnValidationErrors(string query)
    {
        using var response = await client.GetAsync("/api/logs?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task FilteredPaginationKeepsTheSnapshotAndOriginalResponseFields()
    {
        var file = Path.Combine(paths.LogsDirectory, "winpilot20261004.log");
        File.WriteAllText(file, "[10:00:00.000] [INF] Test\nfirst\n\n[10:00:01.000] [DBG] Test\nskip\n\n[10:00:02.000] [ERR] Test\nlast\n\n");
        var first = await client.GetFromJsonAsync<JsonElement>("/api/logs?date=2026-10-04&level=inf,err&limit=1");
        Assert.Equal(3, first.GetProperty("snapshot").GetInt32());
        Assert.Equal("last", first.GetProperty("entries")[0].GetProperty("message").GetString());
        Assert.Equal("ERR", first.GetProperty("entries")[0].GetProperty("level").GetString());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        File.AppendAllText(file, "[10:00:03.000] [INF] Test\nnew\n\n");
        var second = await client.GetFromJsonAsync<JsonElement>("/api/logs?date=2026-10-04&level=inf,err&limit=1&offset=1&snapshot=3");
        Assert.Equal("first", second.GetProperty("entries")[0].GetProperty("message").GetString());
        Assert.False(second.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task MissingLogsStayEmptyAndLockedFilesReturnAnExplicitProblem()
    {
        var empty = await client.GetFromJsonAsync<JsonElement>("/api/logs?date=2026-10-04");
        Assert.Equal(0, empty.GetProperty("entries").GetArrayLength());
        var file = Path.Combine(paths.LogsDirectory, "winpilot20261004.log");
        File.WriteAllText(file, "[10:00:00.000] [INF] Test\nentry\n\n");
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var response = await client.GetAsync("/api/logs?date=2026-10-04"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("日志文件暂时无法读取，请稍后重试", problem.GetProperty("detail").GetString());
        }
        File.Delete(file);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>("/api/logs/dates"))!);
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.DisposeAsync();
        Directory.Delete(directory, true);
    }
}
