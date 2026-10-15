using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using WinPilot.Core;
using WinPilot.Features.Files;
using WinPilot.Web;

namespace WinPilot.Test;

public sealed class FileEndpointErrorTest
{
    public static IEnumerable<object[]> Win32Cases =>
    [
        [2, 404, "文件或文件夹不存在"], [3, 404, "文件或文件夹不存在"],
        [5, 403, "无法访问此项目，没有足够的权限"], [65, 403, "无法访问此项目，没有足够的权限"],
        [19, 403, "项目为只读，无法修改"],
        [32, 409, "文件正在被其他程序使用，请稍后重试"], [33, 409, "文件正在被其他程序使用，请稍后重试"],
        [80, 409, "目标位置已存在同名文件或文件夹"], [183, 409, "目标位置已存在同名文件或文件夹"],
        [87, 400, "路径无效"], [123, 400, "路径无效"], [206, 400, "路径或文件名过长"],
        [0, 500, "文件操作失败"], [9999, 500, "文件操作失败"]
    ];

    public static IEnumerable<object[]> ShellCases()
    {
        foreach (var row in Win32Cases.Where(row => (int)row[0] != 183))
            foreach (var aborted in new[] { false, true }) yield return [row[0], aborted, row[1], row[2]];
        object[][] rows =
        [
            [0x78, 403, "无法访问此项目，没有足够的权限"],
            [0x82, 403, "项目所在位置为只读，无法修改"], [0x83, 403, "项目所在位置为只读，无法修改"],
            [0x86, 403, "项目所在位置为只读，无法修改"], [0x87, 403, "项目所在位置为只读，无法修改"],
            [0x7E, 409, "目标位置已存在同名文件或文件夹"], [0x80, 409, "目标位置已存在同名文件或文件夹"],
            [0x71, 400, "源和目标位置无效"], [0x76, 400, "源和目标位置无效"], [0x7D, 400, "源和目标位置无效"],
            [0x74, 400, "不能对驱动器根目录执行此操作"], [0x10074, 400, "不能对驱动器根目录执行此操作"],
            [0x79, 400, "路径或文件名过长"], [0x81, 400, "路径或文件名过长"], [0xB7, 400, "路径或文件名过长"],
            [0x7C, 400, "路径无效"], [0x85, 400, "文件过大，目标位置无法存储"],
            [0x72, 500, "文件操作失败"], [0x73, 500, "文件操作失败"], [0x75, 500, "文件操作失败"],
            [0x7A, 500, "文件操作失败"], [0x84, 500, "文件操作失败"], [0x88, 500, "文件操作失败"],
            [0x402, 500, "文件操作失败"], [0x10000, 500, "文件操作失败"],
            [0x10005, 500, "文件操作失败"], [0x10020, 500, "文件操作失败"],
            [unchecked((int)0x80070005), 500, "文件操作失败"], [int.MinValue, 500, "文件操作失败"]
        ];
        foreach (var row in rows)
            foreach (var aborted in new[] { false, true }) yield return [row[0], aborted, row[1], row[2]];
    }

    [Theory]
    [MemberData(nameof(Win32Cases))]
    public void Win32ExceptionUsesNativeErrorCode(int code, int status, string detail)
        => Assert.Equal((status, detail), FileEndpointRouteBuilderExtensions.ClassifyFailure(new Win32Exception(code, "内部异常详情")));

    [Theory]
    [MemberData(nameof(Win32Cases))]
    public void IOExceptionUsesOnlyRecognizedWin32HResult(int code, int status, string detail)
        => Assert.Equal((status, detail), FileEndpointRouteBuilderExtensions.ClassifyFailure(new IOException("内部异常详情", unchecked((int)(0x80070000u | (uint)code)))));

    [Theory]
    [MemberData(nameof(ShellCases))]
    public void ShellCodesTakePrecedenceAndAbortedFlagDoesNotOverrideCause(int code, bool aborted, int status, string detail)
        => Assert.Equal((status, detail), FileEndpointRouteBuilderExtensions.ClassifyFailure(new ShellFileOperationException("内部异常详情", code, aborted)));

    [Theory]
    [InlineData(unchecked((int)0x80100005))]
    [InlineData(unchecked((int)0x80100020))]
    [InlineData(unchecked((int)0x80030020))]
    [InlineData(0x10005)]
    [InlineData(0x70005)]
    public void NonWin32HResultIsNotClassifiedByLowBits(int result)
        => Assert.Equal((500, "文件操作失败"), FileEndpointRouteBuilderExtensions.ClassifyFailure(new IOException("内部异常详情", result)));

    public static IEnumerable<object[]> ExceptionCases =>
    [
        [new FileNotFoundException("内部路径"), 404, "文件或文件夹不存在"],
        [new DirectoryNotFoundException("内部路径"), 404, "文件或文件夹不存在"],
        [new UnauthorizedAccessException("内部路径"), 403, "无法访问此项目，没有足够的权限"],
        [new PathTooLongException("内部路径"), 400, "路径或文件名过长"],
        [new ArgumentException("内部路径"), 400, "路径无效"],
        [new NotSupportedException("内部路径"), 400, "路径无效"],
        [new ResourceNameConflictException("目标已存在：内部路径"), 409, "目标位置已存在同名文件或文件夹"],
        [new FileEditConflictException("编辑冲突：内部路径"), 409, "文件在编辑期间已被其他程序修改"],
        [new InvalidOperationException("目标已存在：内部路径"), 400, "文件操作失败"],
        [new InvalidOperationException("文件在编辑期间已被其他程序修改"), 400, "文件操作失败"],
        [new InvalidOperationException("名称不能为空，且不能使用 . 或 .."), 400, "名称不能为空，且不能使用 . 或 .."],
        [new InvalidOperationException("此文件不支持编辑"), 400, "此文件不支持编辑"],
        [new IOException("内部路径"), 500, "文件操作失败"],
        [new BadHttpRequestException("请求体超过限制", 413), 400, "文件操作失败"]
    ];

    [Theory]
    [MemberData(nameof(ExceptionCases))]
    public void TypedExceptionsClassifyWithoutInferringConflictFromMessage(Exception exception, int status, string detail)
        => Assert.Equal((status, detail), FileEndpointRouteBuilderExtensions.ClassifyFailure(exception));

    [Theory]
    [InlineData("io")]
    [InlineData("win32")]
    [InlineData("shell")]
    public async Task ServerErrorResponseHidesDiagnosticsWhileLogsKeepOriginalException(string kind)
    {
        const string secret = "C:\\private\\内部目录\\secret.txt";
        Exception exception = kind switch
        {
            "shell" => new ShellFileOperationException(secret, 0x10005, true),
            "win32" => new Win32Exception(9999, secret),
            _ => new IOException(secret)
        };
        var logger = new RecordingLogger();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/failure", () => FileEndpointRouteBuilderExtensions.CreateFailureResult(logger, exception, "测试文件操作", secret));
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/failure");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(500, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("文件操作失败", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("文件操作失败", json.RootElement.GetProperty("detail").GetString());
        Assert.Equal(new[] { "detail", "status", "title", "type" }, json.RootElement.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(LogLevel.Error, logger.Level);
        Assert.Same(exception, logger.Exception);
        Assert.Contains(secret, logger.Message);
        if (kind == "shell")
        {
            Assert.Contains("65541", logger.Message);
            Assert.Contains("True", logger.Message);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public LogLevel Level { get; private set; }
        public Exception? Exception { get; private set; }
        public string Message { get; private set; } = "";
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Level = level; Exception = exception; Message = formatter(state, exception); }
    }
}
