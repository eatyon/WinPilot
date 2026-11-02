namespace WinPilot.Features.Programs;

/// <summary>
/// 统一启动目标与工作目录校验，供实际启动及需要同等校验的保存入口使用。
/// </summary>
internal static class ProgramLaunchValidator
{
    internal static void Validate(string? programPath, string? workingDirectory)
    {
        ValidateProgramPath(programPath);
        ValidateWorkingDirectory(workingDirectory);
    }

    internal static void ValidateProgramPath(string? programPath)
    {
        ValidateProgramPathFormat(programPath);
        if (!File.Exists(programPath))
        {
            throw new InvalidOperationException("找不到指定的程序文件");
        }
    }

    /// <summary>
    /// 仅检查非空、绝对路径和受支持的后缀，不检查文件是否存在。
    /// </summary>
    internal static void ValidateProgramPathFormat(string? programPath)
    {
        if (string.IsNullOrWhiteSpace(programPath))
        {
            throw new InvalidOperationException("请输入程序路径");
        }
        if (!Path.IsPathFullyQualified(programPath))
        {
            throw new InvalidOperationException("程序路径必须是绝对路径");
        }
        if (Path.GetExtension(programPath).ToLowerInvariant() is not (".exe" or ".bat" or ".cmd" or ".vbs" or ".py"))
        {
            throw new InvalidOperationException("不支持此文件类型");
        }
    }

    internal static void ValidateWorkingDirectory(string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
        {
            throw new InvalidOperationException("起始目录不存在");
        }
    }
}
