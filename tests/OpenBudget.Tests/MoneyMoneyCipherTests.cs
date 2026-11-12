using Microsoft.Data.Sqlite;
using OpenBudget.Importers.MoneyMoney;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Decrypting a MoneyMoney database: a synthetic database encrypted with MoneyMoney's SQLCipher settings opens with
/// the right password (or MoneyMoney's default key when none is set) and is refused with a wrong one.
/// </summary>
public sealed class MoneyMoneyCipherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ob-mm-cipher-{Guid.NewGuid():N}");

    public MoneyMoneyCipherTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* best effort */ }
    }

    /// <summary>A minimal MoneyMoney schema with a LOCALIZED-collated column (as MoneyMoney declares securities.ident).</summary>
    internal static void WritePlainSnapshot(string path, bool withStatements = true)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        conn.CreateCollation("LOCALIZED", string.CompareOrdinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE accounts (name TEXT, iban TEXT, currency TEXT, type INTEGER, portfolio INTEGER, balances TEXT, sort INTEGER, refresh_tsp INTEGER, error TEXT, group_key INTEGER);
            CREATE TABLE account_groups (name TEXT, group_key INTEGER, sort INTEGER);
            CREATE TABLE category_groups (name TEXT, group_key INTEGER, sort INTEGER, color TEXT);
            CREATE TABLE categories (name TEXT, type INTEGER, color TEXT, rules TEXT, group_key INTEGER, sort INTEGER);
            CREATE TABLE transactions (local_account_key INTEGER, amount REAL, currency TEXT, timestamp INTEGER, name TEXT, comment TEXT, unformatted_purpose TEXT, category_key INTEGER, checkmark INTEGER, splitted INTEGER);
            CREATE TABLE securities (account_key INTEGER, name TEXT, ident TEXT COLLATE LOCALIZED, quantity REAL);
            INSERT INTO categories (name, type) VALUES ('', 1);
            INSERT INTO categories (name, type) VALUES ('Groceries', 1);
            INSERT INTO accounts (name, currency, type, balances) VALUES ('Example Checking', 'EUR', 1, '');
            INSERT INTO transactions (local_account_key, amount, currency, timestamp, name, category_key, checkmark, splitted) VALUES (1, -12.5, 'EUR', 1780000000, 'Example Shop', 2, 0, 0);
            INSERT INTO securities (account_key, name, ident, quantity) VALUES (1, 'Example Fund', 'XX0000000001', 3);
            """;
        cmd.ExecuteNonQuery();
        if (withStatements)
        {
            cmd.CommandText = """
                CREATE TABLE statements (rowid INTEGER PRIMARY KEY, account_key INTEGER, name TEXT, directory TEXT, filename TEXT, created_tsp INTEGER, deleted INTEGER);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    private string Encrypted(string key)
    {
        var plain = Path.Combine(_dir, "plain.sqlite");
        WritePlainSnapshot(plain);
        var encrypted = Path.Combine(_dir, "MoneyMoney.sqlite");
        MoneyMoneyCipher.Encrypt(plain, encrypted, key);
        File.Delete(plain);
        return encrypted;
    }

    private static long Count(string path, string table)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
#pragma warning disable CA2100 // the table names are the test's own constants
        cmd.CommandText = $"SELECT count(*) FROM {table}";
#pragma warning restore CA2100
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void The_encrypted_file_is_not_readable_as_plain_SQLite()
    {
        var encrypted = Encrypted("secret");
        var header = File.ReadAllBytes(encrypted).AsSpan(0, 16).ToArray();
        Assert.NotEqual("SQLite format 3\0"u8.ToArray(), header);
    }

    [Fact]
    public void The_right_password_decrypts_every_table()
    {
        var encrypted = Encrypted("secret");
        var plain = Path.Combine(_dir, "out.sqlite");

        MoneyMoneyCipher.Decrypt(encrypted, plain, "secret");

        Assert.Equal(1, Count(plain, "accounts"));
        Assert.Equal(1, Count(plain, "transactions"));
        Assert.Equal(2, Count(plain, "categories"));
        Assert.Equal(1, Count(plain, "securities"));
        Assert.Equal(0, Count(plain, "statements"));
        // portfolio_chart is missing in this database; the copy goes on without it
        Assert.Equal("SQLite format 3\0"u8.ToArray(), File.ReadAllBytes(plain).AsSpan(0, 16).ToArray());
    }

    [Fact]
    public void Without_a_password_MoneyMoneys_default_key_is_used()
    {
        var encrypted = Encrypted(MoneyMoneyCipher.DefaultKey);
        var plain = Path.Combine(_dir, "out.sqlite");

        MoneyMoneyCipher.Decrypt(encrypted, plain, "");

        Assert.Equal(1, Count(plain, "accounts"));
    }

    [Fact]
    public void A_wrong_password_is_refused()
    {
        var encrypted = Encrypted("secret");
        var plain = Path.Combine(_dir, "out.sqlite");

        Assert.Throws<MoneyMoneyWrongPasswordException>(() => MoneyMoneyCipher.Decrypt(encrypted, plain, "not-it"));
    }
}
