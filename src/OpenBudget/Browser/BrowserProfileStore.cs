using System.IO.Compression;
using OpenBudget.Connectors;

namespace OpenBudget.Browser;

/// <summary>
/// Long-lived Chromium profiles, one per key (a bank login: <see cref="ForLogin"/>), kept between browser runs so a
/// bank's site sees the same browser each time (its local storage, IndexedDB, service workers and the state of bot
/// checks such as hCaptcha). A profile is stored as a zip of its user-data directory without caches, encrypted with the
/// secrets key (<see cref="SecretsProtector"/>) and bound to its key, in one file per key. Without a secrets key nothing
/// is stored. A stored profile that cannot be read (another key, damage, too large) is ignored and the browser starts
/// with a fresh profile.
/// </summary>
public sealed class BrowserProfileStore
{
    /// <summary>The largest profile archive that is kept; a larger one is not stored (the next run starts fresh).</summary>
    public const long MaxArchiveBytes = 64L * 1024 * 1024;

    /// <summary>The most a profile archive may unpack to (guards against archives that expand enormously).</summary>
    public const long MaxUnpackedBytes = 16 * MaxArchiveBytes;

    /// <summary>Directories of the profile that only hold caches, crash reports or downloaded components.</summary>
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "DawnCache", "DawnGraphiteCache",
        "DawnWebGPUCache", "CacheStorage", "ScriptCache", "Crashpad", "Crash Reports", "BrowserMetrics", "component_crx_cache",
        "extensions_crx_cache", "optimization_guide_model_store", "OptimizationHints", "Safe Browsing", "OnDeviceHeadSuggestModel",
        "segmentation_platform", "MEIPreload", "WidevineCdm", "hyphen-data", "ZxcvbnData", "TrustTokenKeyCommitments",
        "FirstPartySetsPreloaded", "OriginTrials", "SSLErrorAssistant", "CertificateRevocation", "PKIMetadata", "Subresource Filter",
        "AutofillStates", "CookieReadinessList", "privacy_sandbox_attestations", "screen_ai", "Download Service",
    };

    /// <summary>Files Chromium uses to guard a running profile (links on Linux); a copy must not carry them.</summary>
    private static readonly HashSet<string> SkippedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "SingletonLock", "SingletonSocket", "SingletonCookie", "lockfile", "LOCK", "RunningChromeVersion", "BrowserMetrics-spare.pma",
    };

    private readonly string _dir;
    private readonly SecretsProtector _secrets;
    private readonly ILogger<BrowserProfileStore> _logger;

    public BrowserProfileStore(string directory, SecretsProtector secrets, ILogger<BrowserProfileStore> logger)
    {
        _dir = directory;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>The profile key of a bank login.</summary>
    public static string ForLogin(int bankLoginId) => "bank-login-" + bankLoginId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Whether profiles are kept at all (a secrets key is configured).</summary>
    public bool IsEnabled => _secrets.IsConfigured;

    public bool Exists(string key) => File.Exists(PathOf(key));

    /// <summary>
    /// Unpacks the stored profile of <paramref name="key"/> into the empty directory <paramref name="target"/>; false (and
    /// <paramref name="target"/> left empty) when none is stored or it cannot be read.
    /// </summary>
    public bool Restore(string key, string target)
    {
        var zip = ReadArchive(key);
        if (zip == null) return false;
        try
        {
            Extract(zip, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "The stored browser profile {Key} could not be restored; the browser starts with a fresh profile.", key);
            Empty(target);
            return false;
        }
    }

    /// <summary>
    /// The stored profile of <paramref name="key"/> as a plain zip of its user-data directory (for a browser that runs
    /// elsewhere); null when none is stored or it cannot be read.
    /// </summary>
    public byte[]? ReadArchive(string key)
    {
        var path = PathOf(key);
        if (!IsEnabled || !File.Exists(path)) return null;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxArchiveBytes + 64) throw new InvalidDataException("The stored profile is too large.");
            if (!_secrets.TryDecryptBytes(File.ReadAllBytes(path), Purpose(key), out var zip))
                throw new InvalidDataException("The stored profile could not be decrypted.");
            return zip;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The stored browser profile {Key} could not be read; the browser starts with a fresh profile.", key);
            return null;
        }
    }

    /// <summary>
    /// Stores the zip <paramref name="archive"/> of a user-data directory (from a browser that ran elsewhere) as the profile
    /// of <paramref name="key"/>, with the same filtering and size limit as <see cref="Save"/>. False when nothing was stored.
    /// </summary>
    public bool SaveArchive(string key, byte[] archive)
    {
        if (!IsEnabled) return false;
        var dir = Path.Combine(Path.GetTempPath(), "openbudget-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            Extract(archive, dir);
            return Save(key, dir);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "The browser profile {Key} could not be stored.", key);
            return false;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a temp directory */ }
        }
    }

    /// <summary>Unpacks <paramref name="zip"/> into <paramref name="target"/>, refusing entries that point outside it.</summary>
    private static void Extract(byte[] zip, string target)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var dest = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!dest.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("The stored profile has an entry outside its directory.");
            if (entry.FullName.EndsWith('/', StringComparison.Ordinal))
            {
                Directory.CreateDirectory(dest);
                continue;
            }
            total += entry.Length;
            if (total > MaxUnpackedBytes) throw new InvalidDataException("The profile is too large when unpacked.");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    /// <summary>
    /// Stores the user-data directory <paramref name="source"/> (of a closed browser) as the profile of <paramref name="key"/>,
    /// replacing the previous one. False when nothing was stored (no secrets key, too large, an error).
    /// </summary>
    public bool Save(string key, string source)
    {
        if (!IsEnabled || !Directory.Exists(source)) return false;
        try
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                Add(archive, new DirectoryInfo(source), "");
            if (buffer.Length > MaxArchiveBytes)
            {
                _logger.LogWarning("The browser profile {Key} is too large to keep ({Bytes} bytes); the next run starts with a fresh profile.", key, buffer.Length);
                Delete(key);
                return false;
            }
            var payload = _secrets.EncryptBytes(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), Purpose(key));
            Directory.CreateDirectory(_dir);
            var path = PathOf(key);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, payload);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The browser profile {Key} could not be stored.", key);
            return false;
        }
    }

    /// <summary>Forgets the stored profile of <paramref name="key"/>; the next browser for it starts fresh.</summary>
    public void Delete(string key)
    {
        try { File.Delete(PathOf(key)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The browser profile {Key} could not be deleted.", key);
        }
    }

    private static void Add(ZipArchive archive, DirectoryInfo dir, string prefix)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            // links (the running profile's guards) and anything else that points elsewhere stay out
            if (entry.LinkTarget != null) continue;
            var name = prefix + entry.Name;
            if (entry is DirectoryInfo sub)
            {
                if (SkippedDirectories.Contains(sub.Name)) continue;
                archive.CreateEntry(name + "/");
                Add(archive, sub, name + "/");
            }
            else if (entry is FileInfo file && !SkippedFiles.Contains(file.Name) && !file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                try { archive.CreateEntryFromFile(file.FullName, name, CompressionLevel.Optimal); }
                catch (FileNotFoundException) { /* removed while being read */ }
            }
        }
    }

    private static void Empty(string dir)
    {
        try
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo d) d.Delete(recursive: true);
                else entry.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a fresh directory is used next time */ }
    }

    private static string Purpose(string key) => "browser-profile:" + key;

    private string PathOf(string key)
    {
        foreach (var c in key)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-') throw new ArgumentException("Invalid profile key.", nameof(key));
        return Path.Combine(_dir, key + ".profile");
    }
}
