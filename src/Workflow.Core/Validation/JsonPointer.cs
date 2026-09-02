using System.Text.Json.Nodes;

namespace Workflow.Core.Validation;

// RFC 6901 pointer parsing, kept separate from the schema's `^/` pattern check:
// the schema only confirms a pointer starts with a slash, not that `~` escapes
// are well-formed or that segments split correctly. Overlap between two
// pointers is decided by their segment lists, never by string prefix -- `/a`
// and `/ab` share a text prefix but name unrelated fields, while `/a` and
// `/a/b` (different lengths, no shared prefix past segment 0) are ancestor and
// descendant of the same field.
public static class JsonPointer
{
    public static bool TryParse(string raw, out IReadOnlyList<string> segments)
    {
        segments = [];

        if (raw.Length == 0 || raw[0] != '/')
        {
            return false;
        }

        var parts = raw[1..].Split('/');
        var result = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            if (!TryUnescape(part, out var unescaped))
            {
                return false;
            }

            result.Add(unescaped);
        }

        segments = result;
        return true;
    }

    // A pointer overlaps another when one's segment list is a prefix of the
    // other's, equal length included -- equality and ancestor/descendant both
    // count as overlap, a shared segment-list prefix that stops mid-segment
    // does not (`/a` and `/ab` are one segment each, neither a prefix of the
    // other, so they never overlap here).
    public static bool Overlaps(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var shorter = a.Count <= b.Count ? a : b;
        var longer = a.Count <= b.Count ? b : a;

        for (var i = 0; i < shorter.Count; i++)
        {
            if (!string.Equals(shorter[i], longer[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // Walks segments through nested objects and arrays to read a mapping source out
    // of process data. A property present with an explicit JSON null is a resolved
    // value (returns true, value null), never confused with an absent property --
    // only a missing key or an out-of-range/non-numeric array segment counts as
    // unresolved, which is exactly the distinction workflow.mapping_missing needs.
    public static bool TryResolve(JsonNode? root, IReadOnlyList<string> segments, out JsonNode? value)
    {
        JsonNode? current = root;

        foreach (var segment in segments)
        {
            switch (current)
            {
                case JsonObject obj when obj.TryGetPropertyValue(segment, out var next):
                    current = next;
                    break;
                case JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count:
                    current = array[index];
                    break;
                default:
                    value = null;
                    return false;
            }
        }

        value = current;
        return true;
    }

    private static bool TryUnescape(string part, out string unescaped)
    {
        if (!part.Contains('~'))
        {
            unescaped = part;
            return true;
        }

        var builder = new System.Text.StringBuilder(part.Length);
        for (var i = 0; i < part.Length; i++)
        {
            if (part[i] != '~')
            {
                builder.Append(part[i]);
                continue;
            }

            if (i + 1 >= part.Length || (part[i + 1] != '0' && part[i + 1] != '1'))
            {
                unescaped = "";
                return false;
            }

            builder.Append(part[i + 1] == '0' ? '~' : '/');
            i++;
        }

        unescaped = builder.ToString();
        return true;
    }
}
