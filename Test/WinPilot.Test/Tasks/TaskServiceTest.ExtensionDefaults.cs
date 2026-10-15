using WinPilot.Core.Tasks;
using WinPilot.PluginContract;

namespace WinPilot.Test;

public sealed partial class TaskServiceTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtensionPlansUseMissingRequiredParameterDefault(bool returnedPlan)
    {
        await using var fixture = new TaskServiceFixture();
        var error = await RunHostPlanAsync(fixture,
            new(PluginHostActionType.StartProgram, new Dictionary<string, object?> { ["programPath"] = "$param.path" }),
            schema: [new("path", "Path", PluginActionParameterType.Text, Required: true, DefaultValue: "default.exe")],
            returnedPlan: returnedPlan);
        Assert.Null(error);
        Assert.Equal("default.exe", Assert.Single(fixture.Programs.Requests).ProgramPath);
    }

    [Theory]
    [InlineData("missing", true)]
    [InlineData("missing", false)]
    [InlineData("null", false)]
    [InlineData("null", true)]
    [InlineData("empty", false)]
    [InlineData("empty", true)]
    [InlineData("false", false)]
    [InlineData("zero", false)]
    [InlineData("array", false)]
    public async Task ExtensionDefaultsOnlyFillMissingKeysBeforeValidation(string input, bool required)
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("defaults", "Defaults");
        object? explicitValue = input switch { "false" => false, "zero" => 0, "array" => Array.Empty<string>(), "empty" => "", _ => null };
        var type = input switch
        {
            "false" => PluginActionParameterType.Checkbox, "zero" => PluginActionParameterType.Number,
            "array" => PluginActionParameterType.MultiCheckbox, _ => PluginActionParameterType.Text
        };
        object fallback = input switch { "false" => true, "zero" => 7, "array" => new[] { "one" }, _ => "default" };
        var parameters = new Dictionary<string, object?>();
        if (input != "missing") parameters.Add("value", explicitValue);
        var called = false;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((IPluginTaskActionApi)scope).Register([new("run", "Run", [
            new("value", "Value", type, required, fallback, type == PluginActionParameterType.MultiCheckbox ? [new("one", "One")] : null)])],
            (values, _, _) =>
            {
                Assert.True(values.ContainsKey("value"));
                Assert.Equal(input == "missing" ? fallback : explicitValue, values["value"]);
                called = true;
                return Task.FromResult(PluginTaskActionResult.Success());
            });
        ((IPluginTaskActionApi)scope).Register([new("done", "Done")], (_, _, _) =>
        {
            completed.TrySetResult();
            return Task.FromResult(PluginTaskActionResult.Success());
        });
        fixture.Tasks.Create(new("Defaults", true, new(TaskTriggerType.WinPilotStartup), [
            new(TaskActionType.Extension, PluginId: "defaults", ActionId: "run", Parameters: parameters, ContinueOnFailure: true),
            new(TaskActionType.Extension, PluginId: "defaults", ActionId: "done")]));
        var path = Path.Combine(fixture.Paths.TasksDirectory, "Defaults.json");
        var original = File.ReadAllText(path);
        fixture.Tasks.RunNow("Defaults");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var valid = !required || input is not ("null" or "empty");
        Assert.Equal(valid, called);
        if (valid) Assert.Empty(fixture.Logger.Exceptions);
        else Assert.Equal("缺少动作参数：value", Assert.Single(fixture.Logger.Exceptions).Message);
        Assert.Equal(input != "missing", parameters.ContainsKey("value"));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtensionDefaultIsValidatedAgainstResolvedOptions(bool valid)
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("defaults", "Defaults");
        var resolutions = 0;
        var calls = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((IPluginTaskActionApi)scope).Register([new("run", "Run", [
            new("choice", "Choice", PluginActionParameterType.Select, DefaultValue: "old", Options: [new("old", "Old")])])],
            (values, _, _) =>
            {
                Assert.Equal(1, resolutions);
                Assert.Equal("old", values["choice"]);
                calls++;
                return Task.FromResult(PluginTaskActionResult.Success());
            }, (_, _, _) =>
            {
                resolutions++;
                return Task.FromResult<IReadOnlyList<PluginActionOption>>(valid ? [new("old", "Old")] : [new("new", "New")]);
            });
        ((IPluginTaskActionApi)scope).Register([new("done", "Done")], (_, _, _) =>
        {
            completed.TrySetResult();
            return Task.FromResult(PluginTaskActionResult.Success());
        });
        fixture.Tasks.Create(new("Defaults", true, new(TaskTriggerType.WinPilotStartup), [
            new(TaskActionType.Extension, PluginId: "defaults", ActionId: "run", ContinueOnFailure: true),
            new(TaskActionType.Extension, PluginId: "defaults", ActionId: "done")]));
        fixture.Tasks.RunNow("Defaults");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, resolutions);
        Assert.Equal(valid ? 1 : 0, calls);
        if (valid) Assert.Empty(fixture.Logger.Exceptions);
        else Assert.Equal("动作参数选项无效：choice", Assert.Single(fixture.Logger.Exceptions).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtensionDefaultCollectionsAreCopiedForEachInvocation(bool list)
    {
        await using var fixture = new TaskServiceFixture();
        using var scope = fixture.Extensions.CreateScope("defaults", "Defaults");
        IList<string> fallback = list ? new List<string> { "one" } : new[] { "one" };
        var called = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((IPluginTaskActionApi)scope).Register([new("run", "Run", [
            new("choices", "Choices", PluginActionParameterType.MultiCheckbox, DefaultValue: fallback, Options: [new("one", "One")])])],
            (values, _, _) =>
            {
                var choices = Assert.IsAssignableFrom<IList<string>>(values["choices"]);
                Assert.NotSame(fallback, choices);
                Assert.Equal(new[] { "one" }, choices);
                choices[0] = "changed";
                called++;
                return Task.FromResult(PluginTaskActionResult.Success());
            });
        ((IPluginTaskActionApi)scope).Register([new("done", "Done")], (_, _, _) =>
        {
            completed.TrySetResult();
            return Task.FromResult(PluginTaskActionResult.Success());
        });
        fixture.Tasks.Create(new("Defaults", true, new(TaskTriggerType.WinPilotStartup), [
            new(TaskActionType.Extension, PluginId: "defaults", ActionId: "run", ContinueOnFailure: true),
            new(TaskActionType.Extension, PluginId: "defaults", ActionId: "run", ContinueOnFailure: true),
            new(TaskActionType.Extension, PluginId: "defaults", ActionId: "done")]));
        fixture.Tasks.RunNow("Defaults");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, called);
        Assert.Equal(new[] { "one" }, fallback);
        Assert.Empty(fixture.Logger.Exceptions);
    }
}
