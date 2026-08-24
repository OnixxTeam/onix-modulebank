using Xunit;

// Integration tests share one PostgreSQL container and one WebApplicationFactory
// for the whole run (see Integration/ApiFactory.cs) and mutate process-wide state
// (environment variables, Json.Schema.Net's global schema registry) while doing
// so -- parallel test classes would race on both.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
