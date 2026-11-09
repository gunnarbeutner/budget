namespace OpenBudget.Auth;

/// <summary>Where OpenBudget keeps its state on disk; shared by the web app and the CLI.</summary>
public static class OpenBudgetPaths
{
    public static string DataDir(string contentRoot) => Path.Combine(contentRoot, "data");

    public static string DatabasePath(string contentRoot) => Path.Combine(DataDir(contentRoot), "budgets.db");

    /// <summary>ASP.NET Core data-protection keys (auth cookies, antiforgery). Must live on the data volume.</summary>
    public static string KeysDir(string contentRoot) => Path.Combine(DataDir(contentRoot), "keys");
}
