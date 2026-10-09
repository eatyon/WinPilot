using System.Text.RegularExpressions;

namespace WinPilot.Features.Updates;

internal sealed partial class SemanticVersion : IComparable<SemanticVersion>
{
    private readonly string[] core;
    private readonly string[] prerelease;
    public string Value { get; }
    public bool IsPrerelease => prerelease.Length > 0;

    private SemanticVersion(string value, string[] core, string[] prerelease)
        => (Value, this.core, this.prerelease) = (value, core, prerelease);

    [GeneratedRegex(@"\A[vV]?(?<core>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex VersionPattern();

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrEmpty(value)) return false;
        var match = VersionPattern().Match(value);
        if (!match.Success) return false;
        var prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : [];
        if (prerelease.Any(part => IsNumeric(part) && part.Length > 1 && part[0] == '0')) return false;
        var normalized = match.Groups["core"].Value + (prerelease.Length > 0 ? "-" + string.Join('.', prerelease) : "");
        version = new(normalized, match.Groups["core"].Value.Split('.'), prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        for (var index = 0; index < core.Length; index++)
        {
            var result = CompareNumeric(core[index], other.core[index]);
            if (result != 0) return result;
        }
        if (!IsPrerelease || !other.IsPrerelease)
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        for (var index = 0; index < Math.Min(prerelease.Length, other.prerelease.Length); index++)
        {
            var left = prerelease[index];
            var right = other.prerelease[index];
            var leftNumeric = IsNumeric(left);
            var rightNumeric = IsNumeric(right);
            var result = leftNumeric && rightNumeric ? CompareNumeric(left, right)
                : leftNumeric != rightNumeric ? leftNumeric ? -1 : 1 : string.CompareOrdinal(left, right);
            if (result != 0) return result;
        }
        return prerelease.Length.CompareTo(other.prerelease.Length);
    }

    private static bool IsNumeric(string value) => value.All(character => character is >= '0' and <= '9');
    private static int CompareNumeric(string left, string right)
        => left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
}
