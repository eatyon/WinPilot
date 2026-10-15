namespace WinPilot.Core;

internal static class FileOperationRecovery
{
    internal static void WithCleanup(Action operation, Action cleanup)
    {
        Exception? failure = null;
        try { operation(); }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            try { cleanup(); }
            catch (Exception exception) when (failure is not null)
            {
                throw new AggregateException("文件保存失败且临时文件清理失败", failure, exception);
            }
        }
    }

    internal static void WithRollback(Action operation, Action rollback)
    {
        try { operation(); }
        catch (Exception failure)
        {
            try { rollback(); }
            catch (Exception exception)
            {
                throw new AggregateException("文件保存失败且重命名回滚失败", failure, exception);
            }
            throw;
        }
    }
}
