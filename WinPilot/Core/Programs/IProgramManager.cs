namespace WinPilot.Core.Programs;

public interface IProgramManager
{
    IReadOnlyList<InteractiveUser> GetInteractiveUsers();
    IReadOnlyList<RunningProgram> GetRunningPrograms(string userName);
    ProgramUsage GetUsage(IReadOnlyList<int> processIds, CancellationToken cancellationToken);
    void Close(IReadOnlyList<int> processIds, bool force);
    void Launch(ProgramLaunchRequest request);
}
