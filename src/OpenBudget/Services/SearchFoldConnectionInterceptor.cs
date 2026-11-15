using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace OpenBudget.Services;

/// <summary>
/// Registers the SQLite function <c>ob_fold</c> (<see cref="SearchText.Fold"/>) on every connection EF opens, so
/// LINQ searches through <see cref="BudgetDbContext.SearchFold"/> run in SQL. A connection opened outside EF
/// (a shared in-memory test connection) is registered with <see cref="Register"/>.
/// </summary>
public sealed class SearchFoldConnectionInterceptor : DbConnectionInterceptor
{
    public static readonly SearchFoldConnectionInterceptor Instance = new();

    public static void Register(SqliteConnection connection) =>
        connection.CreateFunction<string?, string?>(SearchText.SqlFunction, t => t == null ? null : SearchText.Fold(t), isDeterministic: true);

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is SqliteConnection sqlite) Register(sqlite);
    }

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ConnectionOpened(connection, eventData);
        return Task.CompletedTask;
    }
}
