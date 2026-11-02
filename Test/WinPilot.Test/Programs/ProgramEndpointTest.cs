using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinPilot.Core;
using WinPilot.Core.Programs;
using WinPilot.Features.Programs;
using WinPilot.Features.Security;
using WinPilot.Features.Terminal;
using WinPilot.Infrastructure;
using WinPilot.Web;
using WinPilot.Windows;

namespace WinPilot.Test;

public sealed class ProgramEndpointTest
{
    private static string CreateProgramFile(string directory, string name)
    {
        var folder = Path.Combine(directory, "programs");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, name);
        File.WriteAllText(file, "test program");
        return file;
    }

    [Theory]
    [InlineData("", "请输入程序路径")]
    [InlineData(" ", "请输入程序路径")]
    [InlineData("app.exe", "程序路径必须是绝对路径")]
    [InlineData(".\\app.exe", "程序路径必须是绝对路径")]
    [InlineData("C:app.exe", "程序路径必须是绝对路径")]
    [InlineData("\\app.exe", "程序路径必须是绝对路径")]
    [InlineData("C:\\app.txt", "不支持此文件类型")]
    public async Task DirectLaunchRejectsInvalidProgramPaths(string path, string error)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Program.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var programs = new ProgramManager();
            await using var app = await CreateTagTestApp(PortableDataPaths.Create(directory), programs);
            using var client = app.GetTestClient();
            using var response = await client.PostAsJsonAsync("/api/programs/launch", new ProgramLaunchRequest(path, "", "", false));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, programs.Launches);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(error, problem.RootElement.GetProperty("detail").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Theory]
    [InlineData("", "请输入程序路径")]
    [InlineData("app.exe", "程序路径必须是绝对路径")]
    [InlineData("C:app.exe", "程序路径必须是绝对路径")]
    [InlineData("C:\\app.txt", "不支持此文件类型")]
    public async Task TagCreateAndEditRejectInvalidProgramPathsWithoutChangingTheOriginal(string path, string error)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Program.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            var service = new ProgramTagService(paths);
            service.Create(new("Original", CreateProgramFile(directory, "app.exe"), "", "", false));
            var file = Path.Combine(paths.TagsDirectory, "Original.json");
            var original = File.ReadAllText(file);
            var programs = new ProgramManager();
            await using var app = await CreateTagTestApp(paths, programs);
            using var client = app.GetTestClient();
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
            {
                using var request = new HttpRequestMessage(method, method == HttpMethod.Post ? "/api/programs/tags" : "/api/programs/tags/Original")
                {
                    Content = JsonContent.Create(new ProgramTag("Changed", path, "", "", false))
                };
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(error, problem.RootElement.GetProperty("detail").GetString());
                Assert.Equal(original, File.ReadAllText(file));
                Assert.Single(Directory.GetFiles(paths.TagsDirectory));
                Assert.Equal(0, programs.Launches);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Theory]
    [InlineData("app.exe", "程序路径必须是绝对路径")]
    [InlineData("C:\\app.txt", "不支持此文件类型")]
    public async Task ExistingInvalidTagRemainsReadableButCannotBeLaunched(string path, string error)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Program.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            Directory.CreateDirectory(paths.TagsDirectory);
            var file = Path.Combine(paths.TagsDirectory, "Existing.json");
            var tag = new ProgramTag("Existing", path, "", "", false);
            var content = JsonSerializer.Serialize(tag);
            File.WriteAllText(file, content);
            var programs = new ProgramManager();
            await using var app = await CreateTagTestApp(paths, programs);
            using var client = app.GetTestClient();
            Assert.Equal(tag, Assert.Single((await client.GetFromJsonAsync<ProgramTag[]>("/api/programs/tags"))!));
            using var response = await client.PostAsync("/api/programs/tags/Existing/launch", null);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(error, problem.RootElement.GetProperty("detail").GetString());
            Assert.Equal(0, programs.Launches);
            Assert.Equal(content, File.ReadAllText(file));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Theory]
    [InlineData("app.exe")]
    [InlineData("script.BAT")]
    [InlineData("script.cmd")]
    [InlineData("script.vbs")]
    [InlineData("script.py")]
    public async Task TagCreateEditAndDirectLaunchAcceptExistingSupportedFiles(string filename)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Program.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            var programs = new ProgramManager();
            await using var app = await CreateTagTestApp(paths, programs);
            using var client = app.GetTestClient();
            var tag = new ProgramTag("Original", CreateProgramFile(directory, filename), "--example", directory, false);
            using var created = await client.PostAsJsonAsync("/api/programs/tags", tag);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var updated = tag with { Name = "Renamed" };
            using var edited = await client.PutAsJsonAsync("/api/programs/tags/Original", updated);
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
            Assert.Equal(updated, new ProgramTagService(paths).Get("Renamed"));
            Assert.Equal(0, programs.Launches);
            using var launched = await client.PostAsJsonAsync(
                "/api/programs/launch",
                new ProgramLaunchRequest(tag.ProgramPath, tag.Arguments, tag.WorkingDirectory, false));
            Assert.Equal(HttpStatusCode.NoContent, launched.StatusCode);
            Assert.Equal(1, programs.Launches);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Theory]
    [InlineData("file", "找不到指定的程序文件")]
    [InlineData("directory", "起始目录不存在")]
    [InlineData("file-as-directory", "起始目录不存在")]
    public async Task SaveEditAndLaunchShareExistenceChecksWithoutChangingSavedTags(string kind, string error)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Program.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            var service = new ProgramTagService(paths);
            var program = CreateProgramFile(directory, "app.exe");
            var original = new ProgramTag("Original", program, "", "", false);
            service.Create(original);
            var originalFile = Path.Combine(paths.TagsDirectory, "Original.json");
            var originalContent = File.ReadAllText(originalFile);
            var invalid = original with
            {
                Name = "Unavailable",
                ProgramPath = kind == "file" ? Path.Combine(directory, "missing.exe") : program,
                WorkingDirectory = kind == "file-as-directory"
                    ? program
                    : kind == "directory" ? Path.Combine(directory, "missing") : ""
            };
            var programs = new WindowsProgramManager(NullLogger<WindowsProgramManager>.Instance);
            await using var app = await CreateTagTestApp(paths, programs);
            using var client = app.GetTestClient();
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
            {
                using var request = new HttpRequestMessage(method, method == HttpMethod.Post ? "/api/programs/tags" : "/api/programs/tags/Original")
                { Content = JsonContent.Create(invalid) };
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(error, problem.RootElement.GetProperty("detail").GetString());
                Assert.Equal(originalContent, File.ReadAllText(originalFile));
                Assert.Single(Directory.GetFiles(paths.TagsDirectory));
            }
            using var launch = await client.PostAsJsonAsync(
                "/api/programs/launch",
                new ProgramLaunchRequest(invalid.ProgramPath, "", invalid.WorkingDirectory, false));
            Assert.Equal(HttpStatusCode.BadRequest, launch.StatusCode);
            using var launchProblem = JsonDocument.Parse(await launch.Content.ReadAsStringAsync());
            Assert.Equal(error, launchProblem.RootElement.GetProperty("detail").GetString());
            File.WriteAllText(Path.Combine(paths.TagsDirectory, "Unavailable.json"), JsonSerializer.Serialize(invalid));
            Assert.Equal(2, service.GetAll().Count);
            using var tagLaunch = await client.PostAsync("/api/programs/tags/Unavailable/launch", null);
            Assert.Equal(HttpStatusCode.BadRequest, tagLaunch.StatusCode);
            using var tagProblem = JsonDocument.Parse(await tagLaunch.Content.ReadAsStringAsync());
            Assert.Equal(error, tagProblem.RootElement.GetProperty("detail").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

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
            var existing = new ProgramTag("Existing", CreateProgramFile(directory, "app.exe"), "", "", false);
            service.Create(existing);
            var original = File.ReadAllText(Path.Combine(paths.TagsDirectory, "Existing.json"));
            var programs = new ProgramManager();
            await using var app = await CreateTagTestApp(paths, programs);
            using var client = app.GetTestClient();
            using var request = new HttpRequestMessage(new HttpMethod(method), "/api/programs/tags/Missing" + suffix);
            if (method == "PUT")
            {
                request.Content = JsonContent.Create(existing with { Name = "Missing" });
            }
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
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
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
            service.Create(new("Original", CreateProgramFile(directory, "original.exe"), "", "", false));
            service.Create(new("Existing", CreateProgramFile(directory, "existing.exe"), "", "", false));
            var files = Directory.GetFiles(paths.TagsDirectory).ToDictionary(path => path, File.ReadAllText);
            var tags = service.GetAll();
            await using var app = await CreateTagTestApp(paths, new ProgramManager());
            using var client = app.GetTestClient();
            using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? "/api/programs/tags" : "/api/programs/tags/Original")
            {
                Content = JsonContent.Create(new ProgramTag("eXisting", CreateProgramFile(directory, "replacement.exe"), "", "", false))
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
            foreach (var file in files)
            {
                Assert.Equal(file.Value, File.ReadAllText(file.Key));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Theory]
    [InlineData("POST", true, "标签名称不能为空")]
    [InlineData("PUT", true, "标签名称不能为空")]
    [InlineData("POST", false, "请输入程序路径")]
    [InlineData("PUT", false, "请输入程序路径")]
    public async Task TagValidationErrorsRemain400BeforeResourceConflictChecks(
        string method,
        bool invalidName,
        string message)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Program.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            var service = new ProgramTagService(paths);
            service.Create(new("Existing", CreateProgramFile(directory, "app.exe"), "", "", false));
            var original = File.ReadAllText(Path.Combine(paths.TagsDirectory, "Existing.json"));
            await using var app = await CreateTagTestApp(paths, new ProgramManager());
            using var client = app.GetTestClient();
            using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? "/api/programs/tags" : "/api/programs/tags/Existing")
            {
                Content = JsonContent.Create(new ProgramTag(
                    invalidName ? "" : "Existing",
                    invalidName ? CreateProgramFile(directory, "app.exe") : "",
                    "",
                    "",
                    false))
            };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(message, problem.RootElement.GetProperty("detail").GetString());
            Assert.Equal(original, File.ReadAllText(Path.Combine(paths.TagsDirectory, "Existing.json")));
            Assert.Single(service.GetAll());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
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
            service.Create(new("Existing", CreateProgramFile(directory, "app.exe"), "", "", false));
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
            if (!fromTag)
            {
                request.Content = JsonContent.Create(new ProgramLaunchRequest(CreateProgramFile(directory, "app.exe"), "", "", false));
            }
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(failure.Message, problem.RootElement.GetProperty("detail").GetString());
            Assert.Equal("Existing", Assert.Single(service.GetAll()).Name);
            Assert.Equal(0, programs.Launches);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
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
            var tag = new ProgramTag("Original", CreateProgramFile(directory, "app.exe"), "-test", "", false);
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
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("missing")]
    public async Task TagLaunchLogsOnlySuccessfulSubmissionWithoutArguments(string outcome)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinPilot.Program.Endpoint.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PortableDataPaths.Create(directory);
            if (outcome != "missing")
            {
                new ProgramTagService(paths).Create(new("TestTag", CreateProgramFile(directory, "app.exe"), "--private-value", "", false));
            }
            var programs = new ProgramManager { LaunchFailure = outcome == "failure" ? new InvalidOperationException("启动失败") : null };
            using var logs = new RollingFileLoggerProvider(paths.LogsDirectory);
            await using var app = await CreateTagTestApp(paths, programs, logs);
            using var client = app.GetTestClient();
            using var response = await client.PostAsync("/api/programs/tags/TestTag/launch", null);
            Assert.Equal(
                outcome switch { "success" => HttpStatusCode.NoContent, "failure" => HttpStatusCode.BadRequest, _ => HttpStatusCode.NotFound },
                response.StatusCode);
            logs.Dispose();
            var text = string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(paths.LogsDirectory, "*.log").Select(File.ReadAllText));
            Assert.Equal(outcome == "success" ? 1 : 0, text.Split("已请求启动标签：TestTag").Length - 1);
            Assert.DoesNotContain("--private-value", text);
            if (outcome == "success")
            {
                Assert.Contains(
                    $"[INF] {typeof(ProgramEndpointRouteBuilderExtensions).FullName}{Environment.NewLine}已请求启动标签：TestTag",
                    text);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static async Task<WebApplication> CreateTagTestApp(
        PortableDataPaths paths,
        IProgramManager programs,
        ILoggerProvider? logs = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<IProgramManager>(programs);
        builder.Services.AddSingleton<ProgramTagService>();
        var app = builder.Build();
        try
        {
            app.MapProgramEndpoints();
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
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
            var tag = new ProgramTag("test", CreateProgramFile(directory, "test.exe"), "", "", false);
            var created = await client.PostAsJsonAsync("/api/programs/tags", tag);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("/api/programs/tags/test", created.Headers.Location!.OriginalString);
            Assert.Equal(tag, Assert.Single((await client.GetFromJsonAsync<ProgramTag[]>("/api/programs/tags"))!));

            safeMode.SetEnabled(true);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/programs/tags")).StatusCode);
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await client.PostAsync("/api/programs/tags/test/launch", null)).StatusCode);
            Assert.Equal(1, manager.Launches);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/programs/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/programs/tags", tag)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/programs/tags/test", tag)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/programs/tags/test")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync(
                    "/api/programs/launch",
                    new ProgramLaunchRequest(CreateProgramFile(directory, "test.exe"), "", "", false))).StatusCode);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private sealed class ProgramManager : IProgramManager
    {
        public int Launches { get; private set; }
        public Exception? LaunchFailure { get; init; }

        public IReadOnlyList<InteractiveUser> GetInteractiveUsers() => [new("User", true)];

        public IReadOnlyList<RunningProgram> GetRunningPrograms(string userName) => [];

        public ProgramUsage GetUsage(IReadOnlyList<int> processIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Close(IReadOnlyList<int> processIds, bool isGroup) => throw new NotSupportedException();

        public void Launch(ProgramLaunchRequest request)
        {
            if (LaunchFailure is not null)
            {
                throw LaunchFailure;
            }
            Launches++;
        }
    }
}
