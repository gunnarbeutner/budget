namespace OpenBudget.Connectors.Scalable;

/// <summary>
/// The GraphQL documents the connector sends. The data queries are Scalable's public web-client
/// queries (extracted from their website JavaScript), trimmed to the fields OpenBudget needs.
/// </summary>
internal static class ScalableQueries
{
    // ---- cockpit/graphql ----


    public const string FetchCashAccountDetails =
        "query fetchCashAccountDetails($userId: ID!) { account(id: $userId) { id brokerPortfolios { id custodianBank cashAccount { iban bic } } } }";

    public const string GetBrokerPortfolios =
        "query getBrokerPortfolios($userId: ID!) { account(id: $userId) { id brokerPortfolios { id personalizations { name id } } } }";

    public const string GetMailbox =
        "query GetMailbox($userId: ID!) { documentMailbox(userId: $userId) { documents { id date senderName read subject "
        + "... on FileMailboxItem { fileName fileType fileDownloadPath portfolioId } } } }";

    // ---- broker/api/data ----

    public const string MoreTransactions =
        "query moreTransactions($personId: ID!, $input: BrokerTransactionInput!, $portfolioId: ID!) { account(id: $personId) { id brokerPortfolio(id: $portfolioId) { id moreTransactions(input: $input) { ...MoreTransactionsFragment } } } } "
        + "fragment MoreTransactionsFragment on BrokerTransactionSummaries { cursor total transactions { id currency type status isCancellation lastEventDateTime description ...BrokerCashTransactionSummaryFragment ...BrokerNonTradeSecurityTransactionSummaryFragment ...BrokerSecurityTransactionSummaryFragment ...BrokerEltifTransactionSummaryFragment } } "
        + "fragment BrokerCashTransactionSummaryFragment on BrokerCashTransactionSummary { cashTransactionType amount relatedIsin } "
        + "fragment BrokerNonTradeSecurityTransactionSummaryFragment on BrokerNonTradeSecurityTransactionSummary { nonTradeSecurityTransactionType quantity amount isin } "
        + "fragment BrokerSecurityTransactionSummaryFragment on BrokerSecurityTransactionSummary { securityTransactionType quantity amount side isin } "
        + "fragment BrokerEltifTransactionSummaryFragment on BrokerEltifTransactionSummary { amount eltifQuantity isin securityTransactionType side }";

    public const string MoreCryptoTransactions =
        "query moreCryptoTransactions($personId: ID!, $input: CryptoTransactionInput!, $portfolioId: ID!) { account(id: $personId) { id brokerPortfolio(id: $portfolioId) { id crypto { id moreTransactions(input: $input) { ...CryptoMoreTransactionsFragment } } } } } "
        + "fragment CryptoMoreTransactionsFragment on CryptoTransactionSummaries { cursor total transactions { id currency type status lastEventDateTime description ...CryptoNonTradeSecurityTransactionSummaryFragment ...CryptoSecurityTransactionSummaryFragment } } "
        + "fragment CryptoNonTradeSecurityTransactionSummaryFragment on CryptoNonTradeSecurityTransactionSummary { nonTradeSecurityTransactionType quantity coinQuantity amount } "
        + "fragment CryptoSecurityTransactionSummaryFragment on CryptoSecurityTransactionSummary { securityTransactionType quantity coinQuantity ticker amount side }";

    public const string GetCashBalance =
        "query getCashBalance($personId: ID!, $portfolioId: ID!) { account(id: $personId) { id brokerPortfolio(id: $portfolioId) { id payments { id buyingPower { id cashBalance } } } } }";

    public const string GetPortfolioGroupsInventory =
        "query getPortfolioGroupsInventory($personId: ID!, $portfolioId: ID!) { account(id: $personId) { id brokerPortfolio(id: $portfolioId) { id inventory { id "
        + "portfolioGroups { id items { id items { ...SecurityInfoFragment } } } ungroupedInventoryItems { id items { ...SecurityInfoFragment } } } } } } "
        + "fragment SecurityInfoFragment on Security { id isin wkn name type inventory { id position { filled fifoPrice } } quoteTick(includeSinceBuy: false) { id midPrice currency } }";

    public const string GetInventory =
        "query getInventory($personId: ID!, $portfolioId: ID!) { account(id: $personId) { id brokerPortfolio(id: $portfolioId) { id crypto { id coins { id ticker name "
        + "inventory { id etpSecurity { id isin wkn name inventory { id position { filled fifoPrice } } quoteTick(includeSinceBuy: false) { id midPrice currency } } } } } } } }";
}
