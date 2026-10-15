using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    [Theory]
    [InlineData("PUT", "")]
    [InlineData("DELETE", "")]
    [InlineData("POST", "/launch")]
    public async Task MissingTagResourceReturns404WithoutChangingOtherTags(string method, string suffix)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Program.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            var service = new ProgramTagService(paths);
            var existing = new ProgramTag("Existing", "app.exe", "", "", false);
            service.Create(existing);
            var original = File.ReadAllText(Path.Combine(paths.TagsDirectory, "Existing.json"));
            var programs = new ProgramManager();
            await using var app = await CreateTagTestApp(paths, programs);
            using var client = app.GetTestClient();
            using var request = new HttpRequestMessage(new HttpMethod(method), "/api/programs/tags/Missing" + suffix);
            if (method == "PUT") request.Content = JsonContent.Create(existing with { Name = "Missing" });
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("操作失败", problem.RootElement.GetProperty("title").GetString());
            Assert.Equal("标签不存在", problem.RootElement.GetProperty("detail").GetString());
            Assert.Equal(existing, Assert.Single(service.GetAll()));
            Assert.Equal(original, File.ReadAllText(Path.Combine(paths.TagsDirectory, "Existing.json")));
            Assert.Equal(0, programs.Launches);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task DuplicateTagNameReturns409AndPreservesBothTags(string method)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Program.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            var service = new ProgramTagService(paths);
            service.Create(new("Original", "original.exe", "", "", false));
            service.Create(new("Existing", "existing.exe", "", "", false));
            var files = Directory.GetFiles(paths.TagsDirectory).ToDictionary(path => path, File.ReadAllText);
            var tags = service.GetAll();
            await using var app = await CreateTagTestApp(paths, new ProgramManager());
            using var client = app.GetTestClient();
            using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? "/api/programs/tags" : "/api/programs/tags/Original")
            {
                Content = JsonContent.Create(new ProgramTag("eXisting", "replacement.exe", "", "", false))
            };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(409, problem.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("操作失败", problem.RootElement.GetProperty("title").GetString());
            Assert.Equal("同名标签已存在", problem.RootElement.GetProperty("detail").GetString());
            Assert.Equal(tags, service.GetAll());
            Assert.Equal(files.Count, Directory.GetFiles(paths.TagsDirectory).Length);
            foreach (var file in files) Assert.Equal(file.Value, File.ReadAllText(file.Key));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("POST", true, "标签名称不能为空")]
    [InlineData("PUT", true, "标签名称不能为空")]
    [InlineData("POST", false, "请输入程序路径")]
    [InlineData("PUT", false, "请输入程序路径")]
    public async Task TagValidationErrorsRemain400BeforeResourceConflictChecks(string method, bool invalidName, string message)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Program.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            var service = new ProgramTagService(paths);
            service.Create(new("Existing", "app.exe", "", "", false));
            var original = File.ReadAllText(Path.Combine(paths.TagsDirectory, "Existing.json"));
            await using var app = await CreateTagTestApp(paths, new ProgramManager());
            using var client = app.GetTestClient();
            using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? "/api/programs/tags" : "/api/programs/tags/Existing")
            {
                Content = JsonContent.Create(new ProgramTag(invalidName ? "" : "Existing", invalidName ? "app.exe" : "", "", "", false))
            };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(message, problem.RootElement.GetProperty("detail").GetString());
            Assert.Equal(original, File.ReadAllText(Path.Combine(paths.TagsDirectory, "Existing.json")));
            Assert.Single(service.GetAll());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false, "file")]
    [InlineData(true, "file")]
    [InlineData(false, "directory")]
    [InlineData(true, "directory")]
    [InlineData(false, "invalid")]
    [InlineData(true, "invalid")]
    public async Task ProgramLaunchFailuresRemain400IncludingLaunchFromExistingTag(bool fromTag, string kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Program.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            var service = new ProgramTagService(paths);
            service.Create(new("Existing", "app.exe", "", "", false));
            var failure = kind switch
            {
                "file" => (Exception)new FileNotFoundException("标签不存在"),
                "directory" => new DirectoryNotFoundException("工作目录不存在"),
                _ => new InvalidOperationException("同名标签已存在")
            };
            var programs = new ProgramManager { LaunchFailure = failure };
            await using var app = await CreateTagTestApp(paths, programs);
            using var client = app.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, fromTag ? "/api/programs/tags/Existing/launch" : "/api/programs/launch");
            if (!fromTag) request.Content = JsonContent.Create(new ProgramLaunchRequest("app.exe", "", "", false));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(failure.Message, problem.RootElement.GetProperty("detail").GetString());
            Assert.Equal("Existing", Assert.Single(service.GetAll()).Name);
            Assert.Equal(0, programs.Launches);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TagResourceSuccessResponsesPreserve201200204AndLocation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Program.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            var programs = new ProgramManager();
            await using var app = await CreateTagTestApp(paths, programs);
            using var client = app.GetTestClient();
            var tag = new ProgramTag("Original", "app.exe", "-test", "", false);
            using var created = await client.PostAsJsonAsync("/api/programs/tags", tag);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("/api/programs/tags/Original", created.Headers.Location?.OriginalString);
            Assert.Equal(tag, await created.Content.ReadFromJsonAsync<ProgramTag>());
            var renamed = tag with { Name = "Renamed" };
            using var updated = await client.PutAsJsonAsync("/api/programs/tags/Original", renamed);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Assert.Equal(renamed, await updated.Content.ReadFromJsonAsync<ProgramTag>());
            using var launched = await client.PostAsync("/api/programs/tags/Renamed/launch", null);
            Assert.Equal(HttpStatusCode.NoContent, launched.StatusCode);
            Assert.Empty(await launched.Content.ReadAsByteArrayAsync());
            Assert.Equal(1, programs.Launches);
            using var deleted = await client.DeleteAsync("/api/programs/tags/Renamed");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Empty(await deleted.Content.ReadAsByteArrayAsync());
            Assert.Empty((await client.GetFromJsonAsync<ProgramTag[]>("/api/programs/tags"))!);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task<WebApplication> CreateTagTestApp(PortableDataPaths paths, ProgramManager programs)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<IProgramManager>(programs);
        builder.Services.AddSingleton<ProgramTagService>();
        var app = builder.Build();
        try { app.MapProgramEndpoints(); await app.StartAsync(); return app; }
        catch { await app.DisposeAsync(); throw; }
    }

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
        public Exception? LaunchFailure { get; init; }
        public IReadOnlyList<InteractiveUser> GetInteractiveUsers() => [new("User", true)];
        public IReadOnlyList<RunningProgram> GetRunningPrograms(string userName) => [];
        public ProgramUsage GetUsage(IReadOnlyList<int> processIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Close(IReadOnlyList<int> processIds, bool force) => throw new NotSupportedException();
        public void Launch(ProgramLaunchRequest request)
        {
            if (LaunchFailure is not null) throw LaunchFailure;
            Launches++;
        }
    }
}
