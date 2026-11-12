using System.Runtime.InteropServices;
using System.Text;

namespace OpenBudget.Importers.MoneyMoney;

/// <summary>The MoneyMoney database could not be opened with the given password.</summary>
public sealed class MoneyMoneyWrongPasswordException : Exception
{
    public MoneyMoneyWrongPasswordException()
        : base("The MoneyMoney password does not match the database.")
    {
    }

    public MoneyMoneyWrongPasswordException(string message)
        : base(message)
    {
    }

    public MoneyMoneyWrongPasswordException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Decrypts MoneyMoney's SQLCipher database into a plain SQLite file. It talks to the SQLCipher build of SQLite
/// (native <c>e_sqlcipher</c> from SQLitePCLRaw.lib.e_sqlcipher) directly through a handful of C functions, so the
/// app's own SQLite provider (e_sqlite3, used by EF Core and Microsoft.Data.Sqlite) stays untouched.
/// MoneyMoney's cipher settings: the key is the database password ("nopasswd" when none is set), with SQLCipher 4
/// defaults (<c>PRAGMA cipher_compatibility = 4</c>).
/// </summary>
public static class MoneyMoneyCipher
{
    /// <summary>MoneyMoney's key when the user has not set a database password.</summary>
    public const string DefaultKey = "nopasswd";

    /// <summary>SQLCipher major version whose defaults (KDF iterations, HMAC, page size) MoneyMoney uses.</summary>
    public const int CipherCompatibility = 4;

    /// <summary>Tables copied into the plain database: what the importer reads plus the statements list.</summary>
    internal static readonly string[] RequiredTables = ["account_groups", "category_groups", "categories", "accounts", "transactions"];

    /// <summary>Tables absent from some MoneyMoney versions; copied when present.</summary>
    internal static readonly string[] OptionalTables = ["securities", "portfolio_chart", "statements"];

    private const string Library = "e_sqlcipher";
    private const int SqliteOk = 0;
    private const int SqliteUtf8 = 1;
    private const int OpenReadWrite = 0x2;
    private const int OpenCreate = 0x4;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Collation(IntPtr arg, int len1, IntPtr p1, int len2, IntPtr p2);

    // Kept alive for the process: SQLite holds on to the function pointer while a connection is open.
    private static readonly Collation LocalizedCollation = CompareBytes;

    /// <summary>
    /// Opens <paramref name="encryptedPath"/> with <paramref name="password"/> (blank means MoneyMoney's default key)
    /// and writes its tables to a new plain SQLite file at <paramref name="plainPath"/>. Throws
    /// <see cref="MoneyMoneyWrongPasswordException"/> when the key does not open the database.
    /// </summary>
    public static void Decrypt(string encryptedPath, string plainPath, string? password)
    {
        var key = string.IsNullOrEmpty(password) ? DefaultKey : password;
        if (File.Exists(plainPath)) File.Delete(plainPath);
        var db = Open(encryptedPath, OpenReadWrite | OpenCreate);
        try
        {
            Exec(db, $"PRAGMA key = {Quote(key)};");
            Exec(db, $"PRAGMA cipher_compatibility = {CipherCompatibility};");
            // The first read decrypts the header: a wrong key fails here with SQLITE_NOTADB.
            if (TryExec(db, "SELECT count(*) FROM sqlite_master;") is { } error)
                throw new MoneyMoneyWrongPasswordException("The MoneyMoney password does not match the database.", new InvalidDataException(error));

            // MoneyMoney declares some columns COLLATE LOCALIZED, a collation only its own process has. Copying needs a
            // stand-in; the copies are created without the collation, so a byte comparison is enough.
            Check(db, sqlite3_create_collation_v2(db, Utf8("LOCALIZED"), SqliteUtf8, IntPtr.Zero, LocalizedCollation, IntPtr.Zero));

            Exec(db, $"ATTACH DATABASE {Quote(plainPath)} AS plain KEY '';");
            foreach (var table in RequiredTables)
                Exec(db, $"CREATE TABLE plain.{table} AS SELECT * FROM main.{table};");
            foreach (var table in OptionalTables)
                _ = TryExec(db, $"CREATE TABLE plain.{table} AS SELECT * FROM main.{table};");
            Exec(db, "DETACH DATABASE plain;");
        }
        finally
        {
            _ = sqlite3_close_v2(db);
        }
    }

    /// <summary>
    /// Writes the plain SQLite database <paramref name="plainPath"/> to a new database at <paramref name="encryptedPath"/>,
    /// encrypted the way MoneyMoney encrypts its own. Builds synthetic MoneyMoney databases for tests.
    /// </summary>
    internal static void Encrypt(string plainPath, string encryptedPath, string key)
    {
        var db = Open(plainPath, OpenReadWrite | OpenCreate);
        try
        {
            Check(db, sqlite3_create_collation_v2(db, Utf8("LOCALIZED"), SqliteUtf8, IntPtr.Zero, LocalizedCollation, IntPtr.Zero));
            Exec(db, $"PRAGMA cipher_default_compatibility = {CipherCompatibility};");
            Exec(db, $"ATTACH DATABASE {Quote(encryptedPath)} AS encrypted KEY {Quote(key)};");
            Exec(db, "SELECT sqlcipher_export('encrypted');");
            Exec(db, "DETACH DATABASE encrypted;");
        }
        finally
        {
            _ = sqlite3_close_v2(db);
        }
    }

    private static IntPtr Open(string path, int flags)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The database file does not exist.", path);
        // Read-write with create: ATTACHed databases inherit these flags, and the plain copy is a new file.
        var rc = sqlite3_open_v2(Utf8(path), out var db, flags, IntPtr.Zero);
        if (rc != SqliteOk)
        {
            var message = db == IntPtr.Zero ? $"error {rc}" : Message(db);
            _ = sqlite3_close_v2(db);
            throw new InvalidDataException($"The database file could not be opened: {message}");
        }
        return db;
    }

    private static void Exec(IntPtr db, string sql)
    {
        if (TryExec(db, sql) is { } error) throw new InvalidDataException(error);
    }

    /// <summary>Runs <paramref name="sql"/>; null on success, else SQLite's message.</summary>
    private static string? TryExec(IntPtr db, string sql)
    {
        var rc = sqlite3_exec(db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out var errmsg);
        if (rc == SqliteOk) return null;
        var message = errmsg != IntPtr.Zero ? Marshal.PtrToStringUTF8(errmsg) : Message(db);
        if (errmsg != IntPtr.Zero) sqlite3_free(errmsg);
        return $"SQLite error {rc}: {message}";
    }

    private static void Check(IntPtr db, int rc)
    {
        if (rc != SqliteOk) throw new InvalidDataException($"SQLite error {rc}: {Message(db)}");
    }

    private static string Message(IntPtr db) => Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "";

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static byte[] Utf8(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    private static int CompareBytes(IntPtr arg, int len1, IntPtr p1, int len2, IntPtr p2)
    {
        var n = Math.Min(len1, len2);
        for (var i = 0; i < n; i++)
        {
            var a = Marshal.ReadByte(p1, i);
            var b = Marshal.ReadByte(p2, i);
            if (a != b) return a < b ? -1 : 1;
        }
        return len1.CompareTo(len2);
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int sqlite3_close_v2(IntPtr db);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void sqlite3_free(IntPtr p);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr sqlite3_errmsg(IntPtr db);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int sqlite3_create_collation_v2(IntPtr db, byte[] name, int textRep, IntPtr arg, Collation compare, IntPtr destroy);
}
