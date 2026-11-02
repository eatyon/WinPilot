namespace WinPilot.Core;

/// <summary>
/// 为文件保存提供清理与回滚，恢复阶段失败时同时保留原始异常，避免丢失故障原因。
/// </summary>
internal static class FileOperationRecovery
{
    /// <summary>
    /// 无论操作是否成功都执行清理；操作和清理均失败时合并报告。
    /// </summary>
    internal static void WithCleanup(Action operation, Action cleanup)
    {
        Exception? failure = null;
        try
        {
            operation();
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                cleanup();
            }
            catch (Exception exception) when (failure is not null)
            {
                throw new AggregateException("文件保存失败且临时文件清理失败", failure, exception);
            }
        }
    }

    /// <summary>
    /// 仅在操作失败时回滚，回滚成功后继续抛出原异常。
    /// </summary>
    internal static void WithRollback(Action operation, Action rollback)
    {
        try
        {
            operation();
        }
        catch (Exception failure)
        {
            try
            {
                rollback();
            }
            catch (Exception exception)
            {
                throw new AggregateException("文件保存失败且重命名回滚失败", failure, exception);
            }
            throw;
        }
    }
}
