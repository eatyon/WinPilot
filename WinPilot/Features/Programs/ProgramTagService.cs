using System.Text.Json;
using WinPilot.Core;
using WinPilot.Core.Programs;

namespace WinPilot.Features.Programs;

public sealed class ProgramTagService(PortableDataPaths paths)
{
    private readonly object gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public IReadOnlyList<ProgramTag> GetAll()
    {
        lock (gate)
        {
            if (!Directory.Exists(paths.TagsDirectory)) return [];

            return Directory.EnumerateFiles(paths.TagsDirectory, "*.json")
                .Select(Read)
                .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
    }

    public ProgramTag Get(string name)
    {
        lock (gate)
        {
            var file = GetTagFilePath(name);
            if (!File.Exists(file)) throw new FileNotFoundException("标签不存在");
            return Read(file);
        }
    }

    public void Create(ProgramTag tag)
    {
        lock (gate)
        {
            Validate(tag);
            var file = GetTagFilePath(tag.Name);
            if (File.Exists(file)) throw new InvalidOperationException("同名标签已存在");

            Directory.CreateDirectory(paths.TagsDirectory);
            Write(file, tag, false);
        }
    }

    public void Update(string originalName, ProgramTag tag)
    {
        lock (gate)
        {
            Validate(tag);
            var originalFile = GetTagFilePath(originalName);
            if (!File.Exists(originalFile)) throw new FileNotFoundException("标签不存在");

            var nextFile = GetTagFilePath(tag.Name);
            if (!string.Equals(originalFile, nextFile, StringComparison.OrdinalIgnoreCase) && File.Exists(nextFile))
            {
                throw new InvalidOperationException("同名标签已存在");
            }

            if (string.Equals(originalFile, nextFile, StringComparison.OrdinalIgnoreCase))
            {
                Write(originalFile, tag, true);
            }
            else
            {
                File.Move(originalFile, nextFile);
                try { Write(nextFile, tag, true); }
                catch
                {
                    File.Move(nextFile, originalFile);
                    throw;
                }
            }
        }
    }

    public void Delete(string name)
    {
        lock (gate)
        {
            var file = GetTagFilePath(name);
            if (!File.Exists(file)) throw new FileNotFoundException("标签不存在");
            File.Delete(file);
        }
    }

    private ProgramTag Read(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<ProgramTag>(File.ReadAllText(file), JsonOptions)
                ?? throw new InvalidDataException("标签文件内容无效");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"无法读取标签文件：{Path.GetFileName(file)}", exception);
        }
    }

    private static void Write(string file, ProgramTag tag, bool overwrite)
    {
        var temporaryFile = file + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryFile, JsonSerializer.Serialize(tag, JsonOptions));
            File.Move(temporaryFile, file, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
        }
    }

    private string GetTagFilePath(string name)
    {
        ValidateName(name);
        return Path.Combine(paths.TagsDirectory, name + ".json");
    }

    private static void Validate(ProgramTag tag)
    {
        ValidateName(tag.Name);
        if (string.IsNullOrWhiteSpace(tag.ProgramPath)) throw new InvalidOperationException("请输入程序路径");
    }

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("标签名称不能为空");
        if (name is "." or "..") throw new InvalidOperationException("标签名称不能使用 . 或 ..");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
        {
            throw new InvalidOperationException("标签名称包含 Windows 文件名不支持的字符");
        }
        if (ReservedFileNames.Contains(name.Split('.', 2)[0]))
        {
            throw new InvalidOperationException("标签名称不能使用 Windows 保留设备名");
        }
    }
}
