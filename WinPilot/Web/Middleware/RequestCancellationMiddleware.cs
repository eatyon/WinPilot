namespace WinPilot.Web;

/// <summary>
/// 将客户端断开与宿主停止合并为请求取消信号，并在请求结束后恢复原令牌。
/// </summary>
public sealed class RequestCancellationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IHostApplicationLifetime lifetime)
    {
        var originalCancellation = context.RequestAborted;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(originalCancellation, lifetime.ApplicationStopping);
        context.RequestAborted = cancellation.Token;
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            context.RequestAborted = originalCancellation;
        }
    }
}
