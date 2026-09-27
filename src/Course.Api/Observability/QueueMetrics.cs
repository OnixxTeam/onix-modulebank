using Course.Infrastructure.Postgres;
using Course.Observability;

namespace Course.Api.Observability;

// The six series the contract requires of `api`, named and typed exactly as it
// publishes them: five gauges, and one counter whose family is declared without
// the `_total` suffix its sample carries (OpenMetricsDocument enforces that).
//
// No labels at all. Every series here is a whole-queue total, and the contract
// forbids unique identifiers in labels -- a per-process or per-flow breakdown
// would be the first step towards exactly the cardinality it rules out.
public static class QueueMetrics
{
    public static IReadOnlyList<MetricFamily> From(QueueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return
        [
            new MetricFamily(
                "workflow_jobs_ready",
                MetricType.Gauge,
                "Jobs claimable right now, including due retries and expired leases",
                [new MetricSample(snapshot.JobsReady)]),
            new MetricFamily(
                "workflow_job_oldest_age_seconds",
                MetricType.Gauge,
                "Age of the oldest claimable job, zero when none is claimable",
                [new MetricSample(snapshot.JobOldestAgeSeconds)]),
            new MetricFamily(
                "workflow_processes_waiting",
                MetricType.Gauge,
                "Processes waiting for a signal or a manual decision",
                [new MetricSample(snapshot.ProcessesWaiting)]),
            new MetricFamily(
                "outbox_pending",
                MetricType.Gauge,
                "Deliveries pending, leased or waiting to retry",
                [new MetricSample(snapshot.OutboxPending)]),
            new MetricFamily(
                "outbox_oldest_age_seconds",
                MetricType.Gauge,
                "Age of the oldest such delivery, zero when there is none",
                [new MetricSample(snapshot.OutboxOldestAgeSeconds)]),
            new MetricFamily(
                "workflow_failures",
                MetricType.Counter,
                "Terminal workflow failures recorded in stored state",
                [new MetricSample(snapshot.FailuresTotal)]),
        ];
    }
}
