using NUnit.Framework;

// Fixtures run in parallel, each on its own app host and SQLite file rented from AppFixture's pool; the tests of one
// fixture run one after another against that host (each re-seeds it in EndToEndTestBase.SetUp). A fixture that
// touches process-wide state (environment variables) is [NonParallelizable] and runs alone.
// Override the worker count with `-- NUnit.NumberOfTestWorkers=<n>`.
[assembly: Parallelizable(ParallelScope.Fixtures)]
[assembly: LevelOfParallelism(6)]
