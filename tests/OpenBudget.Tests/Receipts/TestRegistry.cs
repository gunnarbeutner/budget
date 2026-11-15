using OpenBudget.Connectors;
using OpenBudget.Connectors.Amazon;
using OpenBudget.Connectors.LidlPlus;

namespace OpenBudget.Tests.Receipts;

/// <summary>The order and receipt sources for services built by hand in tests.</summary>
internal static class TestRegistry
{
    public static readonly ConnectorRegistry Connectors = new(
        [AmazonConnector.CreateRegistration(new AmazonOptions()), LidlPlusConnector.CreateRegistration(new LidlPlusOptions())]);
}
