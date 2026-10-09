namespace WinPilot.Core.Input;

public sealed class KeyboardInputException(string message, Exception? innerException = null) : Exception(message, innerException);
