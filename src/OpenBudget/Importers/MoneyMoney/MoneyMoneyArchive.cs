using System.IO.Compression;

namespace OpenBudget.Importers.MoneyMoney;

/// <summary>What <see cref="MoneyMoneyArchive.Extract"/> found: the (still encrypted) database and the statements folder.</summary>
public sealed record MoneyMoneyFolder(string DatabasePath, string StatementsDirectory, int StatementFiles);

/// <summary>
/// Reads the ZIP of MoneyMoney's data folder (the folder Help › Show Database in Finder opens, compressed in Finder):
/// <c>…/Database/MoneyMoney.sqlite</c> (with its -wal/-shm files when present) and the PDFs under <c>…/Statements/</c>.
/// Only those entries are written, each under the target directory (entries escaping it are skipped), and macOS
/// metadata (<c>__MACOSX/</c>, <c>._*</c>) is ignored.
/// </summary>
public static class MoneyMoneyArchive
{
    /// <summary>The message of the <see cref="InvalidDataException"/> for a ZIP without a MoneyMoney database.</summary>
    public const string NoDatabase = "The ZIP file contains no MoneyMoney database (Database/MoneyMoney.sqlite).";

    /// <summary>The message of the <see cref="InvalidDataException"/> for a file that is not a ZIP.</summary>
    public const string NotAZip = "The file is not a ZIP archive.";

    /// <summary>Most bytes extracted from one archive, against ZIP bombs.</summary>
    public const long MaxExtractedBytes = 20L * 1024 * 1024 * 1024;

    private const string DatabaseFile = "MoneyMoney.sqlite";
    private const string DatabaseEntry = "Database/" + DatabaseFile;

    /// <summary>Extracts the database and statements of <paramref name="zipPath"/> into <paramref name="targetDirectory"/>.</summary>
    public static MoneyMoneyFolder Extract(string zipPath, string targetDirectory)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(NotAZip, ex);
        }
        using (archive)
        {
            var entries = archive.Entries.Where(e => !IsMetadata(e.FullName)).ToList();
            var database = entries
                .Where(e => e.FullName.EndsWith(DatabaseEntry, StringComparison.OrdinalIgnoreCase)
                            && (e.FullName.Length == DatabaseEntry.Length || e.FullName[e.FullName.Length - DatabaseEntry.Length - 1] == '/'))
                .OrderBy(e => e.FullName.Length)
                .FirstOrDefault() ?? throw new InvalidDataException(NoDatabase);
            var prefix = database.FullName[..^DatabaseEntry.Length];

            Directory.CreateDirectory(targetDirectory);
            var root = Path.GetFullPath(targetDirectory);
            var databaseDir = Path.Combine(root, "Database");
            var statementsDir = Path.Combine(root, "Statements");
            Directory.CreateDirectory(databaseDir);
            Directory.CreateDirectory(statementsDir);
            long budget = MaxExtractedBytes;

            var databasePath = Path.Combine(databaseDir, DatabaseFile);
            Write(database, databasePath, ref budget);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = entries.FirstOrDefault(e => string.Equals(e.FullName, database.FullName + suffix, StringComparison.OrdinalIgnoreCase));
                if (sidecar != null) Write(sidecar, databasePath + suffix, ref budget);
            }

            var statementsPrefix = prefix + "Statements/";
            var statements = 0;
            foreach (var entry in entries)
            {
                if (!entry.FullName.StartsWith(statementsPrefix, StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith('/', StringComparison.Ordinal)) continue;
                var relative = entry.FullName[statementsPrefix.Length..];
                var target = Path.GetFullPath(Path.Combine(statementsDir, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(statementsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Write(entry, target, ref budget);
                statements++;
            }
            return new MoneyMoneyFolder(databasePath, statementsDir, statements);
        }
    }

    private static bool IsMetadata(string name) =>
        name.StartsWith("__MACOSX/", StringComparison.Ordinal) || name.Contains("/._", StringComparison.Ordinal) || name.StartsWith("._", StringComparison.Ordinal);

    private static void Write(ZipArchiveEntry entry, string path, ref long budget)
    {
        if (entry.Length > budget) throw new InvalidDataException("The ZIP file unpacks to more data than allowed.");
        budget -= entry.Length;
        using var input = entry.Open();
        using var output = File.Create(path);
        // Copies at most the declared length (+1 byte to notice a lying header).
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            if (written > entry.Length) throw new InvalidDataException("A ZIP entry is larger than declared.");
            output.Write(buffer, 0, read);
        }
    }
}
