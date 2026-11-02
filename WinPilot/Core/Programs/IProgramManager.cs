namespace WinPilot.Core.Programs;

/// <summary>
/// 程序管理的平台契约，供 Web、任务和快捷标签等入口复用。
/// </summary>
public interface IProgramManager
{
    IReadOnlyList<InteractiveUser> GetInteractiveUsers();

    /// <summary>
    /// 查询指定用户的进程快照，包含展示分组、窗口标题和结束权限。
    /// </summary>
    IReadOnlyList<RunningProgram> GetRunningPrograms(string userName);

    ProgramUsage GetUsage(IReadOnlyList<int> processIds, CancellationToken cancellationToken);

    /// <summary>
    /// 结束选中的进程；isGroup 区分整组结束任务与单个结束进程的处理方式。
    /// </summary>
    void Close(IReadOnlyList<int> processIds, bool isGroup);

    void Launch(ProgramLaunchRequest request);
}
