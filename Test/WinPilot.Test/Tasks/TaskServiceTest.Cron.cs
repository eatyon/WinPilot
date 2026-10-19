using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Cronos;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization;
using WinPilot.Web;
using Microsoft.Extensions.Logging;
using WinPilot.Core.Tasks;
using WinPilot.Features.Tasks;

namespace WinPilot.Test;

public sealed partial class TaskServiceTest
{
    [Theory]
    [InlineData("0 */5 * * * *")]
    [InlineData("*/5 * * * * *")]
    [InlineData("* * * * * *")]
    [InlineData("0 0 8 L * MON-FRI")]
    [InlineData("0 0 8 * * 7")]
    [InlineData("0 0 8 * * 1-7")]
    [InlineData("0 0 0 31 2 *")]
    public async Task CronPreviewUsesSchedulerRulesAndLocalTimeWithoutSavingTasks(string expression)
    {
        await using var fixture = new TaskServiceFixture();
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        var before = DateTimeOffset.Now;
        using var response = await client.GetAsync("/api/tasks/cron/preview?expression=" + Uri.EscapeDataString(expression));
        var after = DateTimeOffset.Now;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var times = json.RootElement.GetProperty("nextRuns").EnumerateArray().Select(value => value.GetDateTimeOffset()).ToArray();
        var cron = CronExpression.Parse(expression, CronFormat.IncludeSeconds);
        if (expression == "0 0 0 31 2 *") Assert.Empty(times);
        else
        {
            Assert.Equal(5, times.Length);
            Assert.InRange(times[0], cron.GetNextOccurrence(before, TimeZoneInfo.Local)!.Value,
                cron.GetNextOccurrence(after, TimeZoneInfo.Local)!.Value);
            for (var index = 0; index < times.Length; index++)
            {
                Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(times[index]), times[index].Offset);
                if (index > 0) Assert.Equal(cron.GetNextOccurrence(times[index - 1], TimeZoneInfo.Local), times[index]);
            }
        }
        Assert.Empty(fixture.Tasks.GetAll());
    }

    [Theory]
    [InlineData(null, "请输入 Cron 表达式")]
    [InlineData("", "请输入 Cron 表达式")]
    [InlineData(" ", "请输入 Cron 表达式")]
    [InlineData("invalid", "Cron 表达式格式无效")]
    [InlineData("0 60 * * * *", "Cron 表达式格式无效")]
    [InlineData("0 0 0 8 * * *", "Cron 表达式格式无效")]
    [InlineData("0 8 * * *", "Cron 表达式格式无效")]
    public async Task CronPreviewReturnsFixedValidationErrors(string? expression, string expected)
    {
        await using var fixture = new TaskServiceFixture();
        await using var app = await CreateTaskAppAsync(fixture);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/tasks/cron/preview" +
            (expression is null ? "" : "?expression=" + Uri.EscapeDataString(expression)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, json.RootElement.GetProperty("detail").GetString());
        Assert.Empty(fixture.Tasks.GetAll());
    }

    [Theory]
    [InlineData("0 0/5 * * * *")]
    [InlineData("0/5 * * * * *")]
    [InlineData("0 0 8-18 1,15 * 1-5")]
    [InlineData("0 0 8 1/3 1/2 6,7")]
    [InlineData("0 0 8 * * 0-6")]
    [InlineData("0 0 8 * * 0/2")]
    [InlineData("0 0 8 * * 0,6")]
    [InlineData("0 0 8 * * 1-7")]
    [InlineData("0 0 8 * * 1/2")]
    [InlineData("0 0 8 * * 7")]
    [InlineData("0 0 8 L * MON-FRI")]
    public async Task SixFieldCronCanBeSavedWithoutChangingTheExpression(string expression)
    {
        await using var fixture = new TaskServiceFixture();
        fixture.Tasks.Create(new("generated", false, new(TaskTriggerType.Cron, Cron: expression),
            [new(TaskActionType.SendNotification, NotificationContent: "content")]));
        Assert.Equal(expression, Assert.Single(fixture.Tasks.GetAll()).Trigger.Cron);
    }

    private static async Task<WebApplication> CreateTaskAppAsync(TaskServiceFixture fixture)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Tasks);
        builder.Services.AddSingleton(fixture.Extensions);
        builder.Services.AddSingleton<TaskVariableCatalog>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        var app = builder.Build();
        try
        {
            app.MapTaskEndpoints();
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }
}
