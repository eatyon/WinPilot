using WinPilot.Core.Input;
using WinPilot.Features.Desktop;

namespace WinPilot.Web;

public static class DesktopKeyboardEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDesktopKeyboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/desktop/keyboard", (KeyboardInputRequest request, HttpContext context, KeyboardInputService keyboardInputService, ILoggerFactory loggerFactory) =>
        {
            try
            {
                context.RequestAborted.ThrowIfCancellationRequested();
                keyboardInputService.Send(request.Keys);
                loggerFactory.CreateLogger(typeof(DesktopKeyboardEndpointRouteBuilderExtensions).FullName!)
                    .LogDebug("已发送键盘输入：{Keys}", string.Join(" + ", request.Keys ?? []));
                return Results.NoContent();
            }
            catch (KeyboardInputException exception)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "键盘输入失败", detail: exception.Message);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                loggerFactory.CreateLogger(typeof(DesktopKeyboardEndpointRouteBuilderExtensions).FullName!)
                    .LogError(exception, "发送键盘输入失败");
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "键盘输入失败", detail: "无法发送键盘输入，请稍后重试");
            }
        });

        return endpoints;
    }

    private sealed record KeyboardInputRequest(IReadOnlyList<string>? Keys);
}
