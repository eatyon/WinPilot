using WinPilot.Features.Tasks;

namespace WinPilot.Features.Notifications.CommandBots.Commands;

internal sealed class RunTaskCommand(TaskService? tasks, ILogger logger, string channel, IEnumerable<string>? diagnosticSecrets = null)
{
    internal async Task ExecuteAsync(string argument, string prefix, Func<string, int, Task> reply, CancellationToken token,
        Func<byte[], int, Task>? replyImage = null)
    {
        token.ThrowIfCancellationRequested();
        if (tasks is null)
        {
            await reply("任务服务不可用，请稍后重试", 1);
            return;
        }
        var saved = tasks.GetAll();
        var name = argument.Trim();
        if (name.Length == 0)
        {
            var usage = $"用法：{prefix}运行任务 任务名";
            if (saved.Count == 0)
                await reply($"暂无已保存的任务，请先在任务页面创建任务\n\n{usage}", 1);
            else
                await CommandListReply.SendAsync("已有任务", saved.Select(task => task.Name).ToArray(), usage,
                    reply, replyImage, logger, channel, token, diagnosticSecrets);
            return;
        }
        var task = saved.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (task is null)
        {
            await reply($"未找到任务「{name}」。发送「{prefix}运行任务」查看已有任务和用法", 1);
            return;
        }
        token.ThrowIfCancellationRequested();
        try { tasks.RunNow(task.Name); }
        catch (Exception exception)
        {
            logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, diagnosticSecrets), "{Channel}「运行任务」命令未能提交任务，请检查任务配置和状态", channel);
            var reason = exception is FileNotFoundException ? "任务已不存在，请重新查看任务列表" : "请检查任务配置和状态";
            await reply($"无法提交「运行任务 {task.Name}」命令：{reason}", 1);
            return;
        }
        await reply($"已提交「运行任务 {task.Name}」命令", 1);
    }
}
