using System.Globalization;
using System.Text;

namespace EmpPrep.Services;

/// <summary>
/// Converts Cove tag and performer names into the dotted form trackers expect.
/// </summary>
/// <remarks>
/// A tag is one whitespace-free token, so the words inside a single tag are joined with dots
/// ("Big Tits" becomes "big.tits") and the tag list is then space separated. The distinction
/// matters: dotting the separator instead of the words would merge every tag into one.
/// </remarks>
public static class EmpTagFormatter
{
    /// <summary>Format a single tag name as one dotted token. Returns null for an empty result.</summary>
    public static string? FormatTag(string? name, bool lowercase = true)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var normalized = name.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var pendingSeparator = false;

        foreach (var ch in normalized)
        {
            // Drop the combining marks left by decomposition, so "Anaïs" becomes "anais" rather
            // than carrying accents into a tag the tracker would treat as a different tag.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSeparator && builder.Length > 0)
                    builder.Append('.');
                pendingSeparator = false;
                builder.Append(lowercase ? char.ToLowerInvariant(ch) : ch);
                continue;
            }

            // Everything else — spaces, slashes, punctuation, an existing dot — collapses into a
            // single separator, so "Girl/Girl" and "girl girl" land on the same tag.
            pendingSeparator = true;
        }

        var result = builder.ToString().Trim('.');
        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// Format a whole tag list: dotted, de-duplicated, order preserved, exclusions removed.
    /// </summary>
    public static IReadOnlyList<string> FormatTags(
        IEnumerable<string> names,
        bool lowercase = true,
        IEnumerable<string>? excluded = null)
    {
        var excludedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var exclusion in excluded ?? [])
        {
            var formatted = FormatTag(exclusion, lowercase);
            if (formatted is not null)
                excludedSet.Add(formatted);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();

        foreach (var name in names)
        {
            var formatted = FormatTag(name, lowercase);
            if (formatted is null || excludedSet.Contains(formatted))
                continue;
            if (seen.Add(formatted))
                results.Add(formatted);
        }

        return results;
    }

    /// <summary>Join a formatted tag list into the space-separated string an upload form takes.</summary>
    public static string Join(IEnumerable<string> tags) => string.Join(' ', tags);

    /// <summary>Parse a user-edited tag string back into individual tags, re-normalising each.</summary>
    public static IReadOnlyList<string> ParseUserTags(string? value, bool lowercase = true)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : FormatTags(value.Split([' ', ',', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries), lowercase);
}
