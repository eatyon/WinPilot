using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WinPilot.Core;
using WinPilot.Core.Programs;
using WinPilot.Features.Programs;
using WinPilot.Features.Security;
using WinPilot.Features.Terminal;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class ProgramEndpointTest
{
    [Fact]
    public async Task ProgramRoutesAndSafeModeUseTheSameResourceHierarchy()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Program.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            var manager = new ProgramManager();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton<IProgramManager>(manager);
            builder.Services.AddSingleton<ProgramTagService>();
            builder.Services.AddSingleton<TerminalSessionManager>();
            builder.Services.AddSingleton<SafeModeService>();
            await using var app = builder.Build();
            app.UseMiddleware<SafeModeMiddleware>();
            app.MapProgramEndpoints();
            await app.StartAsync();
            using var client = app.GetTestClient();
            var safeMode = app.Services.GetRequiredService<SafeModeService>();
            safeMode.SetEnabled(false);

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/programs/users")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/program-users")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/program-tags")).StatusCode);
            var tag = new ProgramTag("test", "test.exe", "", "", false);
            var created = await client.PostAsJsonAsync("/api/programs/tags", tag);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("/api/programs/tags/test", created.Headers.Location!.OriginalString);
            Assert.Equal(tag, Assert.Single((await client.GetFromJsonAsync<ProgramTag[]>("/api/programs/tags"))!));

            safeMode.SetEnabled(true);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/programs/tags")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/programs/tags/test/launch", null)).StatusCode);
            Assert.Equal(1, manager.Launches);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/programs/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/programs/tags", tag)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/programs/tags/test", tag)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/programs/tags/test")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/programs/launch", new ProgramLaunchRequest("test.exe", "", "", false))).StatusCode);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class ProgramManager : IProgramManager
    {
        public int Launches { get; private set; }
        public IReadOnlyList<InteractiveUser> GetInteractiveUsers() => [new("User", true)];
        public IReadOnlyList<RunningProgram> GetRunningPrograms(string userName) => [];
        public ProgramUsage GetUsage(IReadOnlyList<int> processIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Close(IReadOnlyList<int> processIds, bool force) => throw new NotSupportedException();
        public void Launch(ProgramLaunchRequest request) => Launches++;
    }
}
