using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenBudget.Importers.MoneyMoney;

namespace OpenBudget.Tests;

/// <summary>
/// Synthetic MoneyMoney data folders as Finder's "Compress" makes them: <c>MoneyMoney/Database/MoneyMoney.sqlite</c>
/// encrypted with MoneyMoney's SQLCipher settings, statement PDFs under <c>MoneyMoney/Statements/&lt;bank&gt;/</c> and
/// macOS metadata entries. Two accounts ("Example Checking", "Example Savings"), three bookings, and one statement of
/// the checking account. Shared by the unit and E2E tests.
/// </summary>
public static class MoneyMoneyZipFixture
{
    public const string Bank = "Example Bank";
    public const string StatementFile = "Statement 2026-01.pdf";

    /// <summary>A small PDF (only the header matters to the import).</summary>
    public static byte[] Pdf(string text) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% {text}\n%%EOF\n");

    /// <summary>The plain MoneyMoney database (unencrypted), written to <paramref name="path"/>.</summary>
    public static void WritePlainDatabase(string path)
    {
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            conn.CreateCollation("LOCALIZED", string.CompareOrdinal);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE accounts (name TEXT COLLATE LOCALIZED, iban TEXT, currency TEXT, type INTEGER, portfolio INTEGER, balances TEXT, sort INTEGER, refresh_tsp INTEGER, error TEXT, group_key INTEGER);
                CREATE TABLE account_groups (name TEXT, group_key INTEGER, sort INTEGER);
                CREATE TABLE category_groups (name TEXT, group_key INTEGER, sort INTEGER, color TEXT);
                CREATE TABLE categories (name TEXT, type INTEGER, color TEXT, rules TEXT, group_key INTEGER, sort INTEGER);
                CREATE TABLE transactions (local_account_key INTEGER, amount REAL, currency TEXT, timestamp INTEGER, name TEXT, comment TEXT, unformatted_purpose TEXT, category_key INTEGER, checkmark INTEGER, splitted INTEGER);
                CREATE TABLE statements (rowid INTEGER PRIMARY KEY, account_key INTEGER, name TEXT, directory TEXT, filename TEXT, created_tsp INTEGER, deleted INTEGER);
                INSERT INTO categories (name, type) VALUES ('', 1);
                INSERT INTO categories (name, type) VALUES ('Groceries', 1);
                INSERT INTO accounts (name, currency, type, balances) VALUES ('Example Checking', 'EUR', 1, '');
                INSERT INTO accounts (name, currency, type, balances) VALUES ('Example Savings', 'EUR', 2, '');
                INSERT INTO transactions (local_account_key, amount, currency, timestamp, name, category_key, checkmark, splitted) VALUES (1, -12.5, 'EUR', 1767312000, 'Example Market', 2, 0, 0);
                INSERT INTO transactions (local_account_key, amount, currency, timestamp, name, category_key, checkmark, splitted) VALUES (1, 2000, 'EUR', 1767398400, 'Example Employer', 1, 0, 0);
                INSERT INTO transactions (local_account_key, amount, currency, timestamp, name, category_key, checkmark, splitted) VALUES (2, 100, 'EUR', 1767484800, 'Transfer', 1, 0, 0);
                INSERT INTO statements (account_key, name, directory, filename, created_tsp, deleted) VALUES (1, 'Statement January', '{Bank}', '{StatementFile}', 1769904000, 0);
                INSERT INTO statements (account_key, name, directory, filename, created_tsp, deleted) VALUES (1, 'Deleted statement', '{Bank}', 'gone.pdf', 1769904000, 1);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    /// <summary>The ZIP bytes, the database encrypted with <paramref name="password"/>.</summary>
    public static byte[] Build(string password, string? workDirectory = null)
    {
        var dir = workDirectory ?? Path.Combine(Path.GetTempPath(), $"ob-mm-zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var plain = Path.Combine(dir, "plain.sqlite");
            var encrypted = Path.Combine(dir, "MoneyMoney.sqlite");
            WritePlainDatabase(plain);
            MoneyMoneyCipher.Encrypt(plain, encrypted, password);
            using var zip = new MemoryStream();
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            {
                archive.CreateEntry("MoneyMoney/");
                archive.CreateEntryFromFile(encrypted, "MoneyMoney/Database/MoneyMoney.sqlite");
                Add(archive, $"MoneyMoney/Statements/{Bank}/{StatementFile}", Pdf("statement january"));
                Add(archive, $"MoneyMoney/Statements/{Bank}/not-listed.pdf", Pdf("not listed"));
                Add(archive, "__MACOSX/MoneyMoney/Database/._MoneyMoney.sqlite", [0, 5, 22, 7]);
                Add(archive, "MoneyMoney/Statements/../../escape.pdf", Pdf("escape"));
            }
            return zip.ToArray();
        }
        finally
        {
            if (workDirectory == null)
            {
                try { Directory.Delete(dir, true); } catch (IOException) { /* best effort */ }
            }
        }
    }

    private static void Add(ZipArchive archive, string name, byte[] content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(content);
    }
}
