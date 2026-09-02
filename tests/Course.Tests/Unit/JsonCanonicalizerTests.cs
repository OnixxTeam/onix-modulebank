using System.Text.Json.Nodes;
using Workflow.Core.Canonicalization;
using Workflow.Core.Maps;

namespace Course.Tests.Unit;

public sealed class JsonCanonicalizerTests
{
    [Fact]
    public void ObjectKeys_AreSortedByOrdinalCodeUnit_RegardlessOfInputOrder()
    {
        var document = JsonNode.Parse("""{"b": 1, "a": 2, "A": 3}""")!;

        var canonical = JsonCanonicalizer.Canonicalize(document);

        Assert.Equal("""{"A":3,"a":2,"b":1}""", canonical);
    }

    [Fact]
    public void NestedObjectsAndArrays_AreCanonicalizedRecursively()
    {
        var document = JsonNode.Parse("""{"outer": {"z": [3, 2, 1], "a": {"y": true, "x": null}}}""")!;

        var canonical = JsonCanonicalizer.Canonicalize(document);

        Assert.Equal("""{"outer":{"a":{"x":null,"y":true},"z":[3,2,1]}}""", canonical);
    }

    [Fact]
    public void Integers_AreFormattedWithoutDecimalPointOrExponent()
    {
        var document = JsonNode.Parse("""{"n": 42, "neg": -7, "zero": 0}""")!;

        var canonical = JsonCanonicalizer.Canonicalize(document);

        Assert.Equal("""{"n":42,"neg":-7,"zero":0}""", canonical);
    }

    [Fact]
    public void Strings_EscapeOnlyQuoteBackslashAndControlCharacters()
    {
        var document = JsonNode.Parse("\"quote:\\\" backslash:\\\\ newline:\\n tab:\\t\"")!;

        var canonical = JsonCanonicalizer.Canonicalize(document);

        Assert.Equal("\"quote:\\\" backslash:\\\\ newline:\\n tab:\\t\"", canonical);
    }

    [Fact]
    public void NonAsciiCharacters_AreWrittenVerbatimNotEscaped()
    {
        var document = JsonNode.Parse("\"café\"")!;

        var canonical = JsonCanonicalizer.Canonicalize(document);

        Assert.Equal("\"café\"", canonical);
    }

    // Ticket 03: "включая случай произвольного вложенного объекта констант" --
    // an arbitrary nested input_constants object must canonicalize regardless
    // of key order or which of JSON/YAML produced the JsonNode tree.
    [Fact]
    public void ArbitraryNestedConstantsObject_CanonicalizesTheSameFromJsonAndYaml()
    {
        var json = MapLoader.Parse("""
            {"input_constants": {"list": [1, "two", {"three": 3, "flag": true}], "nested": {"b": 2, "a": 1}}}
            """);
        var yaml = MapLoader.Parse("""
            input_constants:
              nested:
                a: 1
                b: 2
              list:
                - 1
                - "two"
                - flag: true
                  three: 3
            """);

        Assert.Equal(JsonCanonicalizer.Canonicalize(json), JsonCanonicalizer.Canonicalize(yaml));
    }

    [Fact]
    public void Sha256Hex_IsStableAcrossDifferentKeyOrderingsOfTheSameDocument()
    {
        var first = JsonNode.Parse("""{"a": 1, "b": 2}""")!;
        var second = JsonNode.Parse("""{"b": 2, "a": 1}""")!;

        Assert.Equal(JsonCanonicalizer.Sha256Hex(first), JsonCanonicalizer.Sha256Hex(second));
    }

    [Fact]
    public void Sha256Hex_DiffersWhenAValueChanges()
    {
        var first = JsonNode.Parse("""{"a": 1}""")!;
        var second = JsonNode.Parse("""{"a": 2}""")!;

        Assert.NotEqual(JsonCanonicalizer.Sha256Hex(first), JsonCanonicalizer.Sha256Hex(second));
    }
}
