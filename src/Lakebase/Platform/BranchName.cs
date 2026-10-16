using System.Text;
using System.Text.RegularExpressions;

namespace Lakebase;

/// <summary>
/// Sanitizes names for Lakebase branch ids (lowercase, alphanumeric + hyphens, 3–63 chars).
/// </summary>
public static partial class BranchName
{
    public static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Branch name is required.", nameof(raw));

        var s = raw.Trim().ToLowerInvariant().Replace('_', '-').Replace('/', '-').Replace('.', '-');
        s = InvalidChars().Replace(s, "-");
        s = MultiHyphen().Replace(s, "-").Trim('-');

        if (s.Length < 3)
            s = $"br-{s}";
        if (s.Length > 63)
            s = s[..63].TrimEnd('-');
        if (s.Length < 3)
            throw new InvalidOperationException($"Branch name '{raw}' sanitizes to an invalid id '{s}'.");

        return s;
    }

    public static bool IsProtectedName(string branchName, string mainBranchId)
        => string.Equals(branchName, mainBranchId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(branchName, "production", StringComparison.OrdinalIgnoreCase)
            || string.Equals(branchName, "main", StringComparison.OrdinalIgnoreCase)
            || string.Equals(branchName, "master", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[^a-z0-9-]")]
    private static partial Regex InvalidChars();

    [GeneratedRegex(@"-+")]
    private static partial Regex MultiHyphen();
}
