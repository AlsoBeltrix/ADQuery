using System.Collections.Generic;
using System.Text;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// The reversible encoding for a multi-field aggregation group key (slice1-or-2).
/// <c>grouped_counts</c> is keyed by a single string, so a composite key joins its
/// per-field components with <see cref="Delimiter"/>. Values are free-text directory
/// attributes and may contain that delimiter, so components are escaped on the way in
/// and unescaped on the way out — an unescaped join both merged distinct buckets and,
/// on split, shifted every field after the offending value by one column.
///
/// This type is the single owner of the encoding. The browser's aggregation table
/// (<c>wwwroot/js/app.js</c>) implements the matching decode; keep the two in step.
/// </summary>
internal static class GroupKey
{
    public const char Delimiter = '|';
    public const char Escape = '\\';

    /// <summary>
    /// Joins per-field components into one key. A single component is returned verbatim:
    /// with no join there is no ambiguity to escape, so single-field keys stay readable
    /// for every consumer that displays them raw (headline text, follow-up context).
    /// </summary>
    public static string Compose(IReadOnlyList<string> components)
    {
        if (components.Count <= 1)
        {
            return components.Count == 0 ? string.Empty : components[0];
        }

        var builder = new StringBuilder();
        for (var i = 0; i < components.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(Delimiter);
            }

            builder.Append(EscapeComponent(components[i]));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits a key back into exactly <paramref name="fieldCount"/> components. A key with
    /// too few components (a legacy or malformed key) is padded with empty strings rather
    /// than shifting the caller's columns; extra components are folded into the last field.
    /// </summary>
    public static List<string> Decompose(string key, int fieldCount)
    {
        if (fieldCount <= 1)
        {
            // Mirror of the single-component Compose case: nothing was escaped.
            return [key];
        }

        var components = new List<string>(fieldCount);
        var current = new StringBuilder();
        var escaped = false;

        foreach (var c in key)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
            }
            else if (c == Escape)
            {
                escaped = true;
            }
            else if (c == Delimiter && components.Count < fieldCount - 1)
            {
                components.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (escaped)
        {
            // A trailing lone escape is not valid output of Compose; keep it literally
            // rather than dropping a character.
            current.Append(Escape);
        }

        components.Add(current.ToString());

        while (components.Count < fieldCount)
        {
            components.Add(string.Empty);
        }

        return components;
    }

    private static string EscapeComponent(string component)
    {
        if (component.IndexOf(Delimiter) < 0 && component.IndexOf(Escape) < 0)
        {
            return component;
        }

        var builder = new StringBuilder(component.Length + 4);
        foreach (var c in component)
        {
            if (c == Delimiter || c == Escape)
            {
                builder.Append(Escape);
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
