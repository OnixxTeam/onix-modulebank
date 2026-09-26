using Course.Observability;

namespace Course.Tests.Unit;

// Ticket 07 (week 4): the rendered document is compared byte-for-byte shaped by
// the verifying run -- exact media type, no carriage return anywhere, a
// trailing `# EOF`, a counter declared by family name while its sample carries
// the `_total` suffix, and labels whose cardinality stays bounded. One renderer
// serves `api` and both workers, so these rules are proven once here rather
// than twice over HTTP.
public sealed class OpenMetricsDocumentTests
{
    [Fact]
    public void AnEmptyDocumentIsStillAValidDocument()
    {
        Assert.Equal("# EOF\n", OpenMetricsDocument.Render([]));
    }

    [Fact]
    public void AGaugeIsDeclaredAndSampledUnderItsOwnName()
    {
        var document = OpenMetricsDocument.Render(
        [
            new MetricFamily("workflow_jobs_ready", MetricType.Gauge, "Jobs claimable right now",
                [new MetricSample(3)]),
        ]);

        Assert.Equal(
            """
            # TYPE workflow_jobs_ready gauge
            # HELP workflow_jobs_ready Jobs claimable right now
            workflow_jobs_ready 3
            # EOF

            """.ReplaceLineEndings("\n"),
            document);
    }

    // OpenMetrics declares a counter by family name, without the suffix, and
    // suffixes the sample -- getting this backwards is the single most common
    // way to fail a metrics contract check.
    [Fact]
    public void ACounterIsDeclaredWithoutTheSuffixAndSampledWithIt()
    {
        var document = OpenMetricsDocument.Render(
        [
            new MetricFamily("workflow_failures", MetricType.Counter, "", [new MetricSample(0)]),
        ]);

        Assert.Equal("# TYPE workflow_failures counter\nworkflow_failures_total 0\n# EOF\n", document);
    }

    [Fact]
    public void ACounterFamilyMayNotBeNamedWithTheSuffixItself()
    {
        var families = new MetricFamily[]
        {
            new("workflow_failures_total", MetricType.Counter, "", [new MetricSample(0)]),
        };

        Assert.Throws<ArgumentException>(() => OpenMetricsDocument.Render(families));
    }

    [Fact]
    public void LabelsAreRenderedInOrderAndQuoted()
    {
        var document = OpenMetricsDocument.Render(
        [
            new MetricFamily("outbox_attempts", MetricType.Counter, "",
            [
                new MetricSample(2, [new MetricLabel("outcome", "retry"), new MetricLabel("state", "RETRY_WAIT")]),
            ]),
        ]);

        Assert.Contains("outbox_attempts_total{outcome=\"retry\",state=\"RETRY_WAIT\"} 2\n", document, StringComparison.Ordinal);
    }

    [Fact]
    public void NonIntegerValuesKeepInvariantFormatting()
    {
        var document = OpenMetricsDocument.Render(
        [
            new MetricFamily("outbox_oldest_age_seconds", MetricType.Gauge, "", [new MetricSample(1.5)]),
        ]);

        Assert.Contains("outbox_oldest_age_seconds 1.5\n", document, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocumentNeverContainsACarriageReturn()
    {
        var document = OpenMetricsDocument.Render(
        [
            new MetricFamily("outbox_pending", MetricType.Gauge, "Deliveries in flight", [new MetricSample(1)]),
        ]);

        Assert.DoesNotContain('\r', document);
    }

    [Theory]
    [InlineData("operation_id")]
    [InlineData("operationId")]
    [InlineData("external_request_id")]
    [InlineData("messageId")]
    public void AForbiddenLabelNameIsRejected(string label)
    {
        var families = new MetricFamily[]
        {
            new("outbox_pending", MetricType.Gauge, "", [new MetricSample(1, [new MetricLabel(label, "x")])]),
        };

        Assert.Throws<ArgumentException>(() => OpenMetricsDocument.Render(families));
    }

    [Fact]
    public void ALabelValueThatLooksLikeAUniqueIdentifierIsRejected()
    {
        var families = new MetricFamily[]
        {
            new("outbox_pending", MetricType.Gauge, "",
                [new MetricSample(1, [new MetricLabel("owner", "0b9c1a3e-1f2d-4c5b-8a7e-6d5c4b3a2918")])]),
        };

        Assert.Throws<ArgumentException>(() => OpenMetricsDocument.Render(families));
    }

    [Fact]
    public void ALabelValueLongerThanTheContractLimitIsRejected()
    {
        var families = new MetricFamily[]
        {
            new("outbox_pending", MetricType.Gauge, "",
                [new MetricSample(1, [new MetricLabel("owner", new string('a', 65))])]),
        };

        Assert.Throws<ArgumentException>(() => OpenMetricsDocument.Render(families));
    }

    // "One scrape contains no more than 20 values of one label" is a property of
    // the whole document, not of a single family, so the renderer is the only
    // place that can see it.
    [Fact]
    public void MoreThanTwentyValuesOfOneLabelInOneScrapeIsRejected()
    {
        var samples = Enumerable.Range(0, 21)
            .Select(index => new MetricSample(1, [new MetricLabel("owner", $"owner-{index}")]))
            .ToArray();

        var families = new MetricFamily[] { new("outbox_pending", MetricType.Gauge, "", samples) };

        Assert.Throws<ArgumentException>(() => OpenMetricsDocument.Render(families));
    }

    [Fact]
    public void TwentyValuesOfOneLabelAreStillAccepted()
    {
        var samples = Enumerable.Range(0, 20)
            .Select(index => new MetricSample(1, [new MetricLabel("owner", $"owner-{index}")]))
            .ToArray();

        var document = OpenMetricsDocument.Render([new MetricFamily("outbox_pending", MetricType.Gauge, "", samples)]);

        Assert.EndsWith("# EOF\n", document, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidFamilyNameIsRejected()
    {
        var families = new MetricFamily[] { new("outbox pending", MetricType.Gauge, "", [new MetricSample(1)]) };

        Assert.Throws<ArgumentException>(() => OpenMetricsDocument.Render(families));
    }
}
