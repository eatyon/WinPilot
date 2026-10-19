namespace WinPilot.Features.Tasks;

internal sealed class TaskValidationException(string field, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    internal string Field { get; } = field;
}
