using NUnit.Framework;

// The whole E2E suite shares ONE Kestrel host and ONE temp SQLite file (see AppFixture), with
// per-test isolation provided by re-seeding in EndToEndTestBase.SetUp. That model only holds if
// tests run one at a time — concurrent tests would reseed (delete-all + seed) the shared DB out
// from under each other mid-interaction. Force the assembly fully sequential.
[assembly: NonParallelizable]
[assembly: LevelOfParallelism(1)]
