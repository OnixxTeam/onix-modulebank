using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Course.Observability;

public enum MetricType
{
    Gauge,
    Counter,
}

public sealed record MetricLabel(string Name, string Value);

public sealed record MetricSample(double Value, IReadOnlyList<MetricLabel> Labels)
{
    public MetricSample(double value) : this(value, [])
    {
    }
}

public sealed record MetricFamily(string Name, MetricType Type, string Help, IReadOnlyList<MetricSample> Samples);

// Renders the OpenMetrics 1.0.0 document `api` and both workers publish, and
// refuses to render one that would break the published observability contract.
//
// The rules encoded here are all contract, not taste: the exact media type, no
// carriage return anywhere, a trailing `# EOF`, a counter declared by family
// name while its sample carries the `_total` suffix, no unique identifier as a
// label name or value, label values no longer than 64 characters and no more
// than 20 values of one label in a single scrape.
//
// Violations throw rather than being silently repaired. Every label value this
// solution publishes comes from a closed set (an outcome, a state, an owner
// name), so a violation is a programming error, and a loud failure surfaces it
// in a test run -- whereas a truncated or dropped label would produce a document
// that scrapes cleanly and fails a hidden cardinality check later.
public static class OpenMetricsDocument
{
    public const string MediaType = "application/openmetrics-text; version=1.0.0; charset=utf-8";

    private const int MaximumLabelValueLength = 64;
    private const int MaximumLabelValuesPerScrape = 20;

    private static readonly Regex NamePattern = new("^[A-Za-z_:][A-Za-z0-9_:]*$", RegexOptions.Compiled);
    private static readonly Regex LabelNamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private static readonly Regex UniqueIdentifierPattern = new(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled);

    // The published list. Both spellings the contract names are forbidden, and the
    // camelCase half is derived rather than typed out twice: two hand-written
    // halves are two chances to let them drift.
    private static readonly HashSet<string> ForbiddenLabelNames = BuildForbiddenLabelNames(
    [
        "correlation_id", "request_id", "operation_id", "process_id", "step_instance_id",
        "job_id", "execution_id", "attempt_id", "outbox_id", "external_request_id",
        "message_id", "decision_id",
    ]);

    private static HashSet<string> BuildForbiddenLabelNames(IReadOnlyList<string> snakeCase)
    {
        var names = new HashSet<string>(snakeCase, StringComparer.Ordinal);

        foreach (var name in snakeCase)
        {
            names.Add(ToCamelCase(name));
        }

        return names;
    }

    private static string ToCamelCase(string snakeCase)
    {
        var parts = snakeCase.Split('_');
        return string.Concat(parts.Take(1).Concat(parts.Skip(1).Select(
            part => string.Create(part.Length, part, (span, source) =>
            {
                source.AsSpan().CopyTo(span);
                span[0] = char.ToUpperInvariant(span[0]);
            }))));
    }

    public static string Render(IReadOnlyList<MetricFamily> families)
    {
        ArgumentNullException.ThrowIfNull(families);

        var labelValues = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var builder = new StringBuilder();

        foreach (var family in families)
        {
            AppendFamily(builder, family, labelValues);
        }

        foreach (var (label, values) in labelValues)
        {
            if (values.Count > MaximumLabelValuesPerScrape)
            {
                throw new ArgumentException(
                    $"label '{label}' carries {values.Count} values in one scrape, more than the {MaximumLabelValuesPerScrape} the contract allows",
                    nameof(families));
            }
        }

        builder.Append("# EOF\n");
        return builder.ToString();
    }

    private static void AppendFamily(
        StringBuilder builder,
        MetricFamily family,
        Dictionary<string, HashSet<string>> labelValues)
    {
        if (!NamePattern.IsMatch(family.Name))
        {
            throw new ArgumentException($"'{family.Name}' is not a valid metric family name", nameof(family));
        }

        var (keyword, sampleSuffix) = Declaration(family.Type);

        if (sampleSuffix.Length > 0 && family.Name.EndsWith(sampleSuffix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{keyword} family '{family.Name}' must be declared without the {sampleSuffix} suffix; the sample carries it",
                nameof(family));
        }

        builder.Append("# TYPE ").Append(family.Name).Append(' ').Append(keyword).Append('\n');

        if (!string.IsNullOrEmpty(family.Help))
        {
            builder.Append("# HELP ").Append(family.Name).Append(' ').Append(SingleLine(family.Help)).Append('\n');
        }

        var sampleName = family.Name + sampleSuffix;

        foreach (var sample in family.Samples)
        {
            builder.Append(sampleName);
            AppendLabels(builder, sample.Labels, labelValues);
            builder.Append(' ').Append(FormatValue(sample.Value)).Append('\n');
        }
    }

    private static void AppendLabels(
        StringBuilder builder,
        IReadOnlyList<MetricLabel> labels,
        Dictionary<string, HashSet<string>> labelValues)
    {
        if (labels.Count == 0)
        {
            return;
        }

        builder.Append('{');

        for (var index = 0; index < labels.Count; index++)
        {
            var label = labels[index];
            ValidateLabel(label);

            if (!labelValues.TryGetValue(label.Name, out var values))
            {
                values = new HashSet<string>(StringComparer.Ordinal);
                labelValues[label.Name] = values;
            }

            values.Add(label.Value);

            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(label.Name).Append("=\"").Append(EscapeLabelValue(label.Value)).Append('"');
        }

        builder.Append('}');
    }

    private static void ValidateLabel(MetricLabel label)
    {
        if (!LabelNamePattern.IsMatch(label.Name))
        {
            throw new ArgumentException($"'{label.Name}' is not a valid label name", nameof(label));
        }

        if (ForbiddenLabelNames.Contains(label.Name))
        {
            throw new ArgumentException(
                $"label name '{label.Name}' names a unique identifier, which the contract forbids in metrics",
                nameof(label));
        }

        if (label.Value.Length > MaximumLabelValueLength)
        {
            throw new ArgumentException(
                $"label '{label.Name}' has a value of {label.Value.Length} characters, more than the {MaximumLabelValueLength} the contract allows",
                nameof(label));
        }

        if (UniqueIdentifierPattern.IsMatch(label.Value))
        {
            throw new ArgumentException(
                $"label '{label.Name}' carries a unique identifier as its value, which the contract forbids",
                nameof(label));
        }
    }

    // One table, so the keyword a family declares and the suffix its sample carries
    // cannot disagree: in OpenMetrics a counter is declared by family name and
    // sampled with `_total`, and getting that pair backwards is the single most
    // common way to fail a metrics contract.
    private static (string Keyword, string SampleSuffix) Declaration(MetricType type) => type switch
    {
        MetricType.Gauge => ("gauge", ""),
        MetricType.Counter => ("counter", "_total"),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unknown metric type"),
    };

    // Integral values render without a decimal point (the form every published
    // example uses); anything else keeps invariant formatting so a locale with a
    // decimal comma cannot produce an unparseable sample.
    private static string FormatValue(double value)
    {
        if (double.IsFinite(value) && Math.Abs(value) < 1e15 && value == Math.Truncate(value))
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');

    private static string EscapeLabelValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
