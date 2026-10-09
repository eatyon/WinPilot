using System.Globalization;
using System.Text.RegularExpressions;
using WinPilot.PluginContract;

namespace WinPilot.Features.Tasks;

public static partial class VariableResolver
{
    [GeneratedRegex(@"(?:\$|&)[a-z][a-z0-9-]*(?:\.[a-z][a-z0-9_-]*)*", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();

    internal static IReadOnlyList<string> ExtractNames(string? template) => string.IsNullOrEmpty(template)
        ? []
        : ExtractOccurrences(template).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static IReadOnlyList<string> ExtractOccurrences(string? template) => string.IsNullOrEmpty(template)
        ? []
        : VariablePattern().Matches(template).Select(match => match.Value).ToArray();

    public static string Resolve(string? template, PluginTaskRunContext? context)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        var variables = context?.Variables ?? new Dictionary<string, object?>();
        return string.Join(Environment.NewLine, template.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(line => VariablePattern().Replace(line, match =>
            {
                var name = match.Value;
                if (!variables.TryGetValue(name, out var value) || value is null) return match.Value;
                if (value is PluginImageContent) return string.Empty;
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            })));
    }
}
