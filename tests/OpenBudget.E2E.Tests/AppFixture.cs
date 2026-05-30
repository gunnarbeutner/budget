using NUnit.Framework;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Boots one shared app+Kestrel host for the whole E2E run (expensive to start, cheap to reuse).
/// Per-test data isolation is handled by re-seeding in <see cref="EndToEndTestBase"/>, not by
/// restarting the host.
/// </summary>
[SetUpFixture]
public sealed class AppFixture
{
    public static KestrelWebApplicationFactory Factory { get; private set; } = null!;

    /// <summary>Base URL of the running app, e.g. http://127.0.0.1:51234.</summary>
    public static string BaseUrl => Factory.BaseUrl;

    [OneTimeSetUp]
    public void StartHost()
    {
        Factory = new KestrelWebApplicationFactory();
        // Touch Services to force host creation (CreateHost starts Kestrel and sets BaseUrl).
        _ = Factory.Services;
    }

    [OneTimeTearDown]
    public void StopHost() => Factory.Dispose();
}
