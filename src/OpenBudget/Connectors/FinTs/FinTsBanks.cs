#pragma warning disable S1075 // the banks' FinTS endpoints are fixed, published addresses

namespace OpenBudget.Connectors.FinTs;

/// <summary>
/// The banks that need nothing beyond their <see cref="FinTsBankConfig"/>: each is offered as a plain
/// <see cref="FinTsConnector"/>. A bank with a further channel (a web portal, web banking) has its own connector class.
/// The connector type is stored with each login, so it must not change once a bank is offered.
/// </summary>
public static class FinTsBanks
{
    /// <summary>PSD Bank Nürnberg (Atruvia), FinTS including the electronic statements (HKEKA).</summary>
    public static FinTsBankConfig PsdNuernberg { get; } = new(
        "psd-nuernberg",
        "PSD Bank Nürnberg",
        "PSD Bank Nürnberg over FinTS: accounts (balance, transactions) and electronic account statements as PDF.",
        "76090900",
        new Uri("https://fints2.atruvia.de/cgi-bin/hbciservlet"),
        FallbackBic: "GENODEF1P17",
        UpdInventory: true);

    public static IReadOnlyList<FinTsBankConfig> All { get; } = [PsdNuernberg];

    /// <summary>One registration per bank in <see cref="All"/>.</summary>
    public static IEnumerable<ConnectorRegistration> Registrations(FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null) =>
        All.Select(config => FinTsConnector.Registration(config, options, transportFactory));
}
