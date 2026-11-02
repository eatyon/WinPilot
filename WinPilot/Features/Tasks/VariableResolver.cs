using System.Globalization;
using System.Text.RegularExpressions;
using WinPilot.PluginContract;

namespace WinPilot.Features.Tasks;

/// <summary>
/// 从任务上下文替换文本变量；未知变量原样保留，图片内容交由通知流程单独处理。
/// </summary>
public static partial class VariableResolver
{
    [GeneratedRegex(@"(?:\$|&)[a-z][a-z0-9-]*(?:\.[a-z][a-z0-9_-]*)*", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();

    internal static IReadOnlyList<string> ExtractNames(string? template) =>
        string.IsNullOrEmpty(template)
            ? []
            : ExtractOccurrences(template).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static IReadOnlyList<string> ExtractOccurrences(string? template) =>
        string.IsNullOrEmpty(template)
            ? []
            : VariablePattern().Matches(template).Select(match => match.Value).ToArray();

    internal static string? Remove(string? template, string name) =>
        template is null
            ? null
            : VariablePattern().Replace(
                template,
                match => match.Value.Equals(name, StringComparison.OrdinalIgnoreCase) ? string.Empty : match.Value);

    public static string Resolve(string? template, PluginTaskRunContext? context)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }
        var variables = context?.Variables ?? new Dictionary<string, object?>();
        return string.Join(Environment.NewLine, template.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(line => VariablePattern().Replace(line, match =>
            {
                var name = match.Value;
                // 上下文未提供的值保留原文；图片对象不能被转成正文字符串。
                if (!variables.TryGetValue(name, out var value) || value is null)
                {
                    return match.Value;
                }
                if (value is PluginImageContent)
                {
                    return string.Empty;
                }
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            })));
    }
}
