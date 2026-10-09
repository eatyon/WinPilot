namespace WinPilot.Web;

public sealed class RequestCancellationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IHostApplicationLifetime lifetime)
    {
        var originalCancellation = context.RequestAborted;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(originalCancellation, lifetime.ApplicationStopping);
        context.RequestAborted = cancellation.Token;
        try { await next(context); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { context.RequestAborted = originalCancellation; }
    }
}
