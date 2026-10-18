namespace EmpPrep.Services;

/// <summary>
/// One part of a description that EMP Prep fills in and keeps current even after the user edits
/// the description: the pack theme, the screens, the hosted images.
/// </summary>
/// <param name="Name">Names the slot's marker in a stored description.</param>
/// <param name="Value">What the slot shows now.</param>
/// <param name="Earlier">
/// What it may have shown when the user copied it into their edit: the value before this save, or
/// the same images before they were uploaded.
/// </param>
public sealed record LiveSlot(string Name, string Value, IReadOnlyList<string> Earlier);

/// <summary>
/// Keeps an edited description following the values EMP Prep fills in.
/// </summary>
/// <remarks>
/// <para>
/// An edited description is stored as the user's text, which on its own would freeze everything in
/// it — a theme typed afterwards, or screens uploaded afterwards, would never reach the post until
/// the edit was thrown away. So when an edit is saved, each filled-in part found in it is swapped
/// for a marker, and the marker is filled with the current value whenever the description is shown.
/// </para>
/// <para>
/// Descriptions saved before markers existed are handled too: a filled-in part is recognised by its
/// earlier forms (e.g. the "not uploaded" placeholders) and replaced with its current value.
/// </para>
/// </remarks>
public static class LiveDescription
{
    private static string Marker(string name) => $"{{empprep:{name}}}";

    /// <summary>The text to store for an edit: each filled-in part found in it replaced by its marker.</summary>
    public static string Templatize(string text, IEnumerable<LiveSlot> slots)
    {
        text = text.Replace("\r\n", "\n");
        foreach (var slot in slots)
        {
            var marker = Marker(slot.Name);
            if (text.Contains(marker, StringComparison.Ordinal))
                continue;

            // Longest first, so a value is never matched by a shorter one inside it.
            var candidates = slot.Earlier.Append(slot.Value)
                .Select(candidate => candidate.Replace("\r\n", "\n").Trim())
                .Where(candidate => candidate.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(candidate => candidate.Length);

            foreach (var candidate in candidates)
            {
                var index = Find(text, candidate);
                if (index < 0)
                    continue;

                text = string.Concat(text.AsSpan(0, index), marker, text.AsSpan(index + candidate.Length));
                break;
            }
        }

        return text;
    }

    /// <summary>
    /// Where <paramref name="value"/> occurs, preferring an occurrence on lines of its own — the way
    /// templates place these parts — so a short theme is not matched inside the title above it.
    /// </summary>
    private static int Find(string text, string value)
    {
        var first = text.IndexOf(value, StringComparison.Ordinal);
        for (var index = first; index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            var end = index + value.Length;
            if ((index == 0 || text[index - 1] == '\n') && (end == text.Length || text[end] == '\n'))
                return index;
        }

        return first;
    }

    /// <summary>A stored edit as shown: every marker filled with its slot's current value.</summary>
    public static string Fill(string stored, IReadOnlyList<LiveSlot> slots)
    {
        var text = Templatize(stored, slots);
        foreach (var slot in slots)
            text = text.Replace(Marker(slot.Name), slot.Value.Replace("\r\n", "\n").Trim(), StringComparison.Ordinal);
        return text;
    }
}
