using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Workflow.Core.Canonicalization;

// RFC 8785 (JSON Canonicalization Scheme): object keys sorted by UTF-16 code
// unit order, no insignificant whitespace, minimal string escaping, numbers
// formatted per ECMAScript Number::toString. This exists so the same map
// document, published once as JSON and once as YAML with a different key
// order, hashes identically -- jsonb storage does not preserve key order,
// so canonicalization has to happen before the document is ever written
// (see db/migrations/020_workflow_definitions.sql).
//
// Every field a workflow map itself declares is a schema-typed integer
// (version, action_version, timeout_ms, delays_ms, ...); the one place an
// author could smuggle in an IEEE-754 double is an arbitrary nested
// `input_constants` object. FormatDouble below covers the finite
// integer/decimal range a hand-authored constant reasonably takes, not the
// full ECMAScript Number::toString grammar (exponential notation, subnormals).
public static class JsonCanonicalizer
{
    public static string Canonicalize(JsonNode? document)
    {
        var builder = new StringBuilder();
        Write(document, builder);
        return builder.ToString();
    }

    public static string Sha256Hex(JsonNode? document)
    {
        var canonical = Canonicalize(document);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    private static void Write(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                WriteObject(obj, builder);
                break;
            case JsonArray array:
                WriteArray(array, builder);
                break;
            case JsonValue value:
                WriteValue(value, builder);
                break;
            default:
                throw new InvalidOperationException($"unsupported JSON node type: {node.GetType()}");
        }
    }

    private static void WriteObject(JsonObject obj, StringBuilder builder)
    {
        builder.Append('{');

        var first = true;
        foreach (var key in obj.Select(pair => pair.Key).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;

            WriteString(key, builder);
            builder.Append(':');
            Write(obj[key], builder);
        }

        builder.Append('}');
    }

    private static void WriteArray(JsonArray array, StringBuilder builder)
    {
        builder.Append('[');

        for (var i = 0; i < array.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            Write(array[i], builder);
        }

        builder.Append(']');
    }

    private static void WriteValue(JsonValue value, StringBuilder builder)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                WriteString(value.GetValue<string>(), builder);
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Number:
                builder.Append(FormatNumber(value));
                break;
            default:
                throw new InvalidOperationException($"unsupported JSON value kind: {value.GetValueKind()}");
        }
    }

    // Tries the exact integral representation first (covers every field the
    // map schema itself defines) and only falls back to double formatting
    // for a genuinely fractional input_constants value. A JsonElement-backed
    // node (from JsonNode.Parse of real JSON text) converts freely between
    // numeric TValue types; a YAML-sourced node keeps the CLR type
    // MapLoader.ScalarToJsonNode picked (int, long, or double) and only
    // TryGetValue's against that exact type, hence trying int before double.
    private static string FormatNumber(JsonValue value)
    {
        if (value.TryGetValue<long>(out var l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i.ToString(CultureInfo.InvariantCulture);
        }

        var d = value.GetValue<double>();
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            throw new InvalidOperationException("RFC 8785 canonicalization does not support NaN or Infinity");
        }

        if (double.IsInteger(d) && Math.Abs(d) < 1e15)
        {
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        }

        return FormatDouble(d);
    }

    // .NET's round-trippable "R" format is the same shortest-round-trip
    // family ECMAScript's Number::toString uses, modulo punctuation: .NET
    // writes "1E+21"/"6.5E-05", ECMAScript writes "1e+21"/"6.5e-5".
    private static string FormatDouble(double d)
    {
        var text = d.ToString("R", CultureInfo.InvariantCulture);

        var eIndex = text.IndexOfAny(['E', 'e']);
        if (eIndex < 0)
        {
            return text;
        }

        var mantissa = text[..eIndex];
        var exponentSign = text[eIndex + 1] == '-' ? "-" : "+";
        var exponentDigits = text[(eIndex + 1)..].TrimStart('+', '-').TrimStart('0');
        if (exponentDigits.Length == 0)
        {
            exponentDigits = "0";
        }

        return $"{mantissa}e{exponentSign}{exponentDigits}";
    }

    // Minimal escaping, matching ECMAScript JSON.stringify: only '"', '\',
    // and control characters are escaped. Every other character -- including
    // non-ASCII -- is written verbatim as UTF-16 and later encoded as UTF-8.
    private static void WriteString(string s, StringBuilder builder)
    {
        builder.Append('"');

        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
