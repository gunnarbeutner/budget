using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace OpenBudget.Importers.MoneyMoney;

public enum MoneyMoneyImportPhase
{
    Extracting,
    Decrypting,
    Importing,
    Statements,
    Done,
    Failed,
}

public enum MoneyMoneyImportError
{
    None,
    WrongPassword,
    NoDatabase,
    NotAZip,
    ImportFinished,
    Failed,
}

/// <summary>Where a user's MoneyMoney import stands; <see cref="Done"/>/<see cref="Total"/> count statements in that phase.</summary>
public sealed record MoneyMoneyImportJob(
    MoneyMoneyImportPhase Phase,
    MoneyMoneyImportError Error = MoneyMoneyImportError.None,
    int Done = 0,
    int Total = 0,
    ImportResult? Result = null,
    MoneyMoneyStatementSummary? Statements = null)
{
    public bool Running => Phase is not (MoneyMoneyImportPhase.Done or MoneyMoneyImportPhase.Failed);
}

/// <summary>
/// The MoneyMoney import from the browser. The ZIP of MoneyMoney's data folder arrives in chunks
/// (<see cref="BeginUpload"/>, <see cref="AppendAsync"/>; resumable at <see cref="Received"/>) into a temporary file
/// under the data directory's uploads folder; <see cref="StartImport"/> then runs in the background, independent of the
/// page: extract (<see cref="MoneyMoneyArchive"/>), decrypt with the password (<see cref="MoneyMoneyCipher"/>), import
/// the database (<see cref="MoneyMoneyImportService"/>) and the statements (<see cref="MoneyMoneyStatementImport"/>).
/// The password lives only in memory for the decryption and is never logged; the ZIP and everything extracted or
/// decrypted are deleted afterwards, also on failure, and leftovers of an interrupted run on the next start.
/// Uploads and jobs are per user: each user has at most one of each.
/// </summary>
public sealed class MoneyMoneyBrowserImport
{
    /// <summary>Largest ZIP accepted.</summary>
    public const long MaxUploadBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>Largest chunk one request may carry (below common reverse-proxy body limits stays the client's job).</summary>
    public const int MaxChunkBytes = 16 * 1024 * 1024;

    private sealed class Upload(string userId, string path, long size)
    {
        public string UserId { get; } = userId;
        public string Path { get; } = path;
        public long Size { get; } = size;
        public long Received { get; set; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    private readonly string _directory;
    private readonly MoneyMoneyImportService _importer;
    private readonly MoneyMoneyStatementImport _statements;
    private readonly ILogger<MoneyMoneyBrowserImport> _logger;
    private readonly CancellationToken _stopping;
    private readonly ConcurrentDictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MoneyMoneyImportJob> _jobs = new(StringComparer.Ordinal);

    /// <summary>Raised with the user id whenever that user's job moves on.</summary>
    public event Action<string>? Changed;

    public MoneyMoneyBrowserImport(MoneyMoneyStaging staging, MoneyMoneyImportService importer, MoneyMoneyStatementImport statements,
        ILogger<MoneyMoneyBrowserImport> logger, IHostApplicationLifetime lifetime)
    {
        _directory = staging.Directory;
        _importer = importer;
        _statements = statements;
        _logger = logger;
        _stopping = lifetime.ApplicationStopping;
    }

    /// <summary>Deletes everything a previous run left in <paramref name="directory"/> (uploads and extracted data).</summary>
    public static void CleanLeftovers(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }

    /// <summary>The user's current or last job; null when they have not started one since the app started.</summary>
    public MoneyMoneyImportJob? GetJob(string userId) => _jobs.GetValueOrDefault(userId);

    /// <summary>Forgets the user's finished job (the page then offers a new upload).</summary>
    public void Dismiss(string userId)
    {
        if (_jobs.TryGetValue(userId, out var job) && !job.Running) _jobs.TryRemove(userId, out _);
    }

    /// <summary>Starts an upload of <paramref name="size"/> bytes for the user, replacing their unfinished one; returns its id.</summary>
    public string BeginUpload(string userId, long size)
    {
        if (size <= 0 || size > MaxUploadBytes) throw new ArgumentOutOfRangeException(nameof(size), "The file is empty or too large.");
        foreach (var (id, old) in _uploads.Where(u => u.Value.UserId == userId).ToList())
        {
            if (_uploads.TryRemove(id, out _)) TryDelete(old.Path);
        }
        Directory.CreateDirectory(_directory);
        var uploadId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var path = Path.Combine(_directory, uploadId + ".zip");
        File.Create(path).Dispose();
        _uploads[uploadId] = new Upload(userId, path, size);
        return uploadId;
    }

    /// <summary>Bytes of the upload stored so far; null for an unknown upload or one of another user.</summary>
    public long? Received(string userId, string uploadId) =>
        _uploads.TryGetValue(uploadId, out var u) && u.UserId == userId ? u.Received : null;

    /// <summary>The upload's total size; null for an unknown upload or one of another user.</summary>
    public long? Size(string userId, string uploadId) =>
        _uploads.TryGetValue(uploadId, out var u) && u.UserId == userId ? u.Size : null;

    /// <summary>
    /// Appends a chunk at <paramref name="offset"/>, which must be where the upload stands (else nothing is written);
    /// returns the bytes stored afterwards, or null for an unknown upload.
    /// </summary>
    public async Task<long?> AppendAsync(string userId, string uploadId, long offset, Stream chunk, CancellationToken ct)
    {
        if (!_uploads.TryGetValue(uploadId, out var upload) || upload.UserId != userId) return null;
        await upload.Lock.WaitAsync(ct);
        try
        {
            if (offset != upload.Received) return upload.Received;
            await using var file = new FileStream(upload.Path, FileMode.Open, FileAccess.Write, FileShare.None);
            file.SetLength(upload.Received);
            file.Seek(upload.Received, SeekOrigin.Begin);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await chunk.ReadAsync(buffer, ct)) > 0)
            {
                written += read;
                if (written > MaxChunkBytes || upload.Received + written > upload.Size)
                    throw new InvalidDataException("The chunk is larger than allowed.");
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await file.FlushAsync(ct);
            upload.Received += written;
            return upload.Received;
        }
        finally
        {
            upload.Lock.Release();
        }
    }

    /// <summary>
    /// Imports the complete upload into <paramref name="sourceName"/> in the background. False when the upload is
    /// unknown or incomplete, or the user's previous job still runs.
    /// </summary>
    public bool StartImport(string userId, string uploadId, string sourceName, string? password)
    {
        if (!_uploads.TryGetValue(uploadId, out var upload) || upload.UserId != userId || upload.Received != upload.Size) return false;
        if (_jobs.TryGetValue(userId, out var running) && running.Running) return false;
        if (!_uploads.TryRemove(uploadId, out _)) return false;
        Set(userId, new MoneyMoneyImportJob(MoneyMoneyImportPhase.Extracting));
        _ = Task.Run(() => RunAsync(userId, upload.Path, sourceName, password), CancellationToken.None);
        return true;
    }

    private async Task RunAsync(string userId, string zipPath, string sourceName, string? password)
    {
        var workDir = zipPath[..^".zip".Length];
        var ct = _stopping;
        try
        {
            var folder = MoneyMoneyArchive.Extract(zipPath, workDir);
            TryDelete(zipPath);

            Set(userId, new MoneyMoneyImportJob(MoneyMoneyImportPhase.Decrypting));
            var plain = Path.Combine(workDir, "plain.sqlite");
            MoneyMoneyCipher.Decrypt(folder.DatabasePath, plain, password);
            TryDelete(folder.DatabasePath);

            Set(userId, new MoneyMoneyImportJob(MoneyMoneyImportPhase.Importing));
            var result = await _importer.ImportFromFileAsync(sourceName, plain, ct);

            Set(userId, new MoneyMoneyImportJob(MoneyMoneyImportPhase.Statements, Result: result));
            var lastReport = DateTime.MinValue;
            var summary = await _statements.ImportFolderAsync(sourceName, plain, folder.StatementsDirectory, (done, total) =>
            {
                if (done < total && DateTime.UtcNow - lastReport < TimeSpan.FromMilliseconds(500)) return;
                lastReport = DateTime.UtcNow;
                Set(userId, new MoneyMoneyImportJob(MoneyMoneyImportPhase.Statements, Done: done, Total: total, Result: result));
            }, ct);

            Set(userId, new MoneyMoneyImportJob(MoneyMoneyImportPhase.Done, Result: result, Statements: summary));
            _logger.LogInformation("MoneyMoney import from the browser into source {Source}: {Transactions} transactions, {Stored} statements stored.",
                sourceName, result.TransactionsImported, summary.Stored);
        }
        catch (MoneyMoneyWrongPasswordException)
        {
            Fail(userId, MoneyMoneyImportError.WrongPassword);
        }
        catch (InvalidDataException ex) when (ex.Message == MoneyMoneyArchive.NoDatabase)
        {
            Fail(userId, MoneyMoneyImportError.NoDatabase);
        }
        catch (InvalidDataException ex) when (ex.Message == MoneyMoneyArchive.NotAZip)
        {
            Fail(userId, MoneyMoneyImportError.NotAZip);
        }
        catch (MoneyMoneyImportFinishedException)
        {
            Fail(userId, MoneyMoneyImportError.ImportFinished);
        }
#pragma warning disable CA1031 // a background job: every failure is reported to the user and logged, never thrown
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "MoneyMoney import from the browser into source {Source} failed.", sourceName);
            Fail(userId, MoneyMoneyImportError.Failed);
        }
        finally
        {
            TryDelete(zipPath);
            TryDeleteDirectory(workDir);
        }
    }

    private void Fail(string userId, MoneyMoneyImportError error) =>
        Set(userId, new MoneyMoneyImportJob(MoneyMoneyImportPhase.Failed, error));

    private void Set(string userId, MoneyMoneyImportJob job)
    {
        _jobs[userId] = job;
        Changed?.Invoke(userId);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort; cleaned on the next start */ }
        catch (UnauthorizedAccessException) { /* best effort; cleaned on the next start */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { /* best effort; cleaned on the next start */ }
        catch (UnauthorizedAccessException) { /* best effort; cleaned on the next start */ }
    }
}

/// <summary>The directory of <see cref="MoneyMoneyBrowserImport"/>'s temporary files.</summary>
public sealed record MoneyMoneyStaging(string Directory);
