using System.Collections.Concurrent;
using NUnit.Framework;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// A pool of app+Kestrel hosts, each over its own temp SQLite file (expensive to start, cheap to reuse). Fixtures run
/// in parallel (see AssemblyInfo); each one rents a host for its whole run, so no two running fixtures ever share a
/// database, and per-test isolation within a fixture comes from re-seeding in <see cref="EndToEndTestBase"/>. At most
/// as many hosts are started as fixtures run at once.
/// </summary>
[SetUpFixture]
public sealed class AppFixture
{
    private static readonly ConcurrentBag<KestrelWebApplicationFactory> Free = [];
    private static readonly ConcurrentBag<KestrelWebApplicationFactory> All = [];

    /// <summary>A host no other running fixture uses; give it back with <see cref="Return"/>.</summary>
    public static KestrelWebApplicationFactory Rent()
    {
        if (Free.TryTake(out var factory)) return factory;
        factory = new KestrelWebApplicationFactory();
        All.Add(factory);
        // Touch Services to force host creation (CreateHost starts Kestrel and sets BaseUrl).
        _ = factory.Services;
        return factory;
    }

    public static void Return(KestrelWebApplicationFactory factory) => Free.Add(factory);

    [OneTimeTearDown]
    public void StopHosts()
    {
        foreach (var factory in All) factory.Dispose();
    }
}
