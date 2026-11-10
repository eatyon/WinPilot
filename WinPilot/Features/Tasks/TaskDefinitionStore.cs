using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPilot.Core;
using WinPilot.Core.Tasks;

namespace WinPilot.Features.Tasks;

/// <summary>
/// 读写任务定义文件；任务状态与重命名回滚由调用方协调。
/// </summary>
internal sealed class TaskDefinitionStore(PortableDataPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal IEnumerable<string> EnumerateFiles() =>
        Directory.Exists(paths.TasksDirectory)
            ? Directory.EnumerateFiles(paths.TasksDirectory, "*.json")
            : [];

    internal WinPilotTask Read(string file)
    {
        var stored = JsonSerializer.Deserialize<TaskPersistenceFile>(File.ReadAllText(file), JsonOptions);
        if (stored is null)
        {
            throw new InvalidDataException("任务文件内容无效");
        }
        return stored.ToTask();
    }

    /// <summary>
    /// 先写入同目录临时文件再替换目标文件，失败时清理临时文件。
    /// </summary>
    internal void Save(WinPilotTask task, bool overwrite = true)
    {
        Directory.CreateDirectory(paths.TasksDirectory);
        var file = GetFilePath(task.Name);
        var temporaryFile = file + $".{Guid.NewGuid():N}.tmp";
        FileOperationRecovery.WithCleanup(() =>
        {
            File.WriteAllText(temporaryFile, JsonSerializer.Serialize(TaskPersistenceFile.From(task), JsonOptions));
            File.Move(temporaryFile, file, overwrite);
        }, () =>
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        });
    }

    internal string GetFilePath(string name)
    {
        TaskDefinitionValidator.ValidateName(name);
        return Path.Combine(paths.TasksDirectory, name + ".json");
    }
}
