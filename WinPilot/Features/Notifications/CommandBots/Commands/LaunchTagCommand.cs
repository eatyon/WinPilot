using System.ComponentModel;
using WinPilot.Core.Programs;
using WinPilot.Features.Programs;

namespace WinPilot.Features.Notifications.CommandBots.Commands;

internal sealed class LaunchTagCommand(ProgramTagService? tags, IProgramManager? programs, ILogger logger, string channel,
    IEnumerable<string>? diagnosticSecrets = null)
{
    internal async Task ExecuteAsync(string argument, string prefix, Func<string, int, Task> reply, CancellationToken token,
        Func<byte[], int, Task>? replyImage = null)
    {
        token.ThrowIfCancellationRequested();
        IReadOnlyList<ProgramTag> saved;
        try { saved = (tags ?? throw new InvalidOperationException()).GetAll(); }
        catch (Exception exception)
        {
            logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, diagnosticSecrets), "{Channel}「启动标签」命令无法读取已保存的程序标签", channel);
            await reply("无法读取标签，请检查已保存的标签配置", 1);
            return;
        }

        var name = argument.Trim();
        if (name.Length == 0)
        {
            var usage = $"用法：{prefix}启动标签 标签名";
            if (saved.Count == 0)
                await reply($"暂无已保存的标签，请先在程序页面保存标签\n\n{usage}", 1);
            else
                await CommandListReply.SendAsync("已有标签", saved.Select(tag => tag.Name).ToArray(), usage,
                    reply, replyImage, logger, channel, token, diagnosticSecrets);
            return;
        }

        var tag = saved.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (tag is null)
        {
            await reply($"未找到标签「{name}」。发送「{prefix}启动标签」查看已有标签和用法", 1);
            return;
        }
        if (programs is null)
        {
            await reply("当前无法启动标签，请稍后重试", 1);
            return;
        }

        token.ThrowIfCancellationRequested();
        try
        {
            programs.Launch(new(tag.ProgramPath, tag.Arguments, tag.WorkingDirectory, tag.RunElevated, tag.Silent));
        }
        catch (Exception exception)
        {
            var reason = exception switch
            {
                FileNotFoundException => "程序文件不存在",
                DirectoryNotFoundException => "起始目录不存在",
                UnauthorizedAccessException => "没有启动权限",
                Win32Exception => "Windows 无法启动该程序，请检查文件和权限",
                _ => "请检查标签配置和程序状态"
            };
            logger.LogWarning(NotificationFailureDiagnostics.Redact(exception, diagnosticSecrets), "{Channel}「启动标签」命令执行失败，原因：{Reason}", channel, reason);
            await reply($"无法提交「启动标签 {tag.Name}」命令：{reason}", 1);
            return;
        }
        await reply($"已提交「启动标签 {tag.Name}」命令", 1);
    }
}
