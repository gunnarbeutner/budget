using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.OneDrive;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Receipts;

public sealed class MailReceiptOptions
{
    /// <summary>The shortest time between two automatic reads of a user's mailbox.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Later reads start this long before the previous one (mails can arrive late).</summary>
    public TimeSpan Overlap { get; set; } = TimeSpan.FromDays(3);

    /// <summary>Most messages looked at in one read.</summary>
    public int MaxMessages { get; set; } = 10_000;

    /// <summary>Largest invoice PDF downloaded.</summary>
    public long MaxAttachmentBytes { get; set; } = 10L * 1024 * 1024;
}

/// <summary>What one mailbox read found: invoice mails, newly stored receipts, PDFs that could not be read, and an error.</summary>
public sealed record MailCheckResult(int Mails, int NewReceipts, int Unreadable, string? Error);

/// <summary>
/// Reads supermarket invoices from the Microsoft 365 mailboxes of users who granted mail access (Microsoft Graph,
/// delegated Mail.Read through their OneDrive connection): mails received since the last read whose subject is a REWE
/// invoice's and that have attachments; only the invoice PDF is downloaded. New invoices are stored and matched with
/// the bookings (see <see cref="ReceiptStore"/>). Runs in the background like bank syncs (server switch and quiet hours
/// of <see cref="BackgroundSyncOptions"/>, at most every <see cref="MailReceiptOptions.Interval"/> per user) and on demand.
/// Mail contents and invoice lines are never logged.
/// </summary>
public sealed class MailReceiptSync : BackgroundService
{
    private const int PageSize = 50;

    private readonly SystemDbContextFactory _system;
    private readonly OneDriveService _graph;
    private readonly ReceiptStore _store;
    private readonly BackgroundSyncOptions _background;
    private readonly MailReceiptOptions _options;
    private readonly TimeProvider _clock;
    private readonly ChangeNotificationService _notifier;
    private readonly ILogger<MailReceiptSync> _logger;
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public MailReceiptSync(
        SystemDbContextFactory system, OneDriveService graph, ReceiptStore store, BackgroundSyncOptions background, MailReceiptOptions options,
        TimeProvider clock, ChangeNotificationService notifier, ILogger<MailReceiptSync> logger)
    {
        _system = system;
        _graph = graph;
        _store = store;
        _background = background;
        _options = options;
        _clock = clock;
        _notifier = notifier;
        _logger = logger;
    }

    public bool IsEnabled => _graph.IsEnabled;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_background.Enabled || !IsEnabled) return;
        try
        {
            await Task.Delay(_background.Tick * (0.5 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000) / 2000.0), _clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Reading receipts from mailboxes failed; trying again at the next tick.");
                }
                await Task.Delay(_background.Tick, _clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    /// <summary>One look: reads the mailboxes that are due (outside the quiet hours); returns how many it read.</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        if (!IsEnabled || BackgroundSyncService.InQuietHours(TimeOnly.FromDateTime(_clock.GetLocalNow().DateTime), _background)) return 0;
        List<string> users;
        Dictionary<string, DateTime?> attempts;
        await using (var db = await _system.CreateDbContextAsync(ct))
        {
            users = await db.CloudConnections.AsNoTracking().Where(c => c.Provider == CloudProviders.OneDrive && c.MailAccess && !c.NeedsReconnect)
                .Select(c => c.OwnerUserId).ToListAsync(ct);
            attempts = await db.ReceiptSettings.AsNoTracking().Where(s => users.Contains(s.OwnerUserId))
                .ToDictionaryAsync(s => s.OwnerUserId, s => s.MailAttemptAt, StringComparer.Ordinal, ct);
        }
        var read = 0;
        foreach (var user in users)
        {
            if (attempts.GetValueOrDefault(user) is { } last && Now - last < _options.Interval) continue;
            await CheckNowAsync(user, ct);
            read++;
        }
        return read;
    }

    /// <summary>Reads the user's mailbox now (one read per user at a time).</summary>
    public async Task<MailCheckResult> CheckNowAsync(string userId, CancellationToken ct = default)
    {
        if (!IsEnabled) return new MailCheckResult(0, 0, 0, "Microsoft is not configured on this server.");
        SemaphoreSlim gate;
        lock (_lock)
        {
            if (!_gates.TryGetValue(userId, out gate!)) _gates[userId] = gate = new SemaphoreSlim(1, 1);
        }
        await gate.WaitAsync(ct);
        try
        {
            return await ReadAsync(userId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<MailCheckResult> ReadAsync(string userId, CancellationToken ct)
    {
        var started = Now;
        DateTime? since;
        await using (var db = await _system.CreateDbContextAsync(ct))
        {
            var connection = await db.CloudConnections.AsNoTracking().FirstOrDefaultAsync(c => c.OwnerUserId == userId && c.Provider == CloudProviders.OneDrive, ct);
            if (connection is not { MailAccess: true }) return new MailCheckResult(0, 0, 0, "Mail access is not granted.");
            var settings = await db.ReceiptSettings.AsNoTracking().FirstOrDefaultAsync(s => s.OwnerUserId == userId, ct);
            // The first read takes the whole mailbox; later ones start shortly before the previous complete read.
            since = settings?.MailCheckedAt is { } checkedAt ? checkedAt - _options.Overlap : null;
        }

        int mails = 0, added = 0, unreadable = 0;
        string? error = null;
        try
        {
            foreach (var message in await ListInvoiceMailsAsync(userId, since, ct))
            {
                mails++;
                await using var db = await _system.CreateDbContextAsync(ct);
                if (await db.ReceiptInvoices.AnyAsync(r => r.OwnerUserId == userId && r.MessageId == message, ct)) continue;
                foreach (var pdf in await InvoicePdfsAsync(userId, message, ct))
                {
                    ParsedReceipt parsed;
                    try
                    {
                        parsed = ReweInvoice.Parse(pdf);
                    }
                    catch (InvalidDataException ex)
                    {
                        unreadable++;
                        _logger.LogWarning(ex, "An invoice PDF in the mailbox of user {UserId} could not be read.", userId);
                        continue;
                    }
                    if (await ReceiptStore.AddAsync(db, userId, ReceiptShops.Rewe, parsed, message, Now, ct)) added++;
                }
            }
            await using (var db = await _system.CreateDbContextAsync(ct))
                await _store.MatchAsync(db, userId, ct);
        }
        catch (CloudException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Reading the mailbox of user {UserId} failed.", userId);
            error = "Microsoft Graph answered unexpectedly; see the server log.";
        }

        await using (var db = await _system.CreateDbContextAsync(ct))
        {
            var settings = await db.ReceiptSettings.FirstOrDefaultAsync(s => s.OwnerUserId == userId, ct);
            if (settings == null)
            {
                settings = new ReceiptSettings { OwnerUserId = userId };
                db.ReceiptSettings.Add(settings);
            }
            settings.MailAttemptAt = Now;
            settings.MailLastError = error is { Length: > 1000 } ? error[..1000] : error;
            // A read with unreadable invoices is not complete: the next one looks at the same mails again.
            if (error == null && unreadable == 0) settings.MailCheckedAt = started;
            await db.SaveChangesAsync(ct);
        }
        _logger.LogInformation("Read the mailbox of user {UserId}: {Mails} invoice mails, {New} new receipts, {Unreadable} unreadable.", userId, mails, added, unreadable);
        _notifier.Publish(new DataChange(DataArea.CloudFiles, UserId: userId));
        if (added > 0) _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: userId));
        return new MailCheckResult(mails, added, unreadable, error);
    }

    /// <summary>The ids of mails received since <paramref name="since"/> (all when null) that look like REWE invoices (subject and attachments).</summary>
    private async Task<List<string>> ListInvoiceMailsAsync(string userId, DateTime? since, CancellationToken ct)
    {
        var filter = (since is { } from ? $"receivedDateTime ge {from.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)} and " : "")
            + $"hasAttachments eq true and startswith(subject,'{ReweInvoice.SubjectPrefix}')";
        string? url = $"{_graph.GraphBase}/me/messages?$filter={Uri.EscapeDataString(filter)}&$select=id,subject,receivedDateTime&$top={PageSize}";
        var ids = new List<string>();
        while (url != null && ids.Count < _options.MaxMessages)
        {
            var pageUrl = url;
            using var response = await _graph.GraphRequestAsync(userId, () => new HttpRequestMessage(HttpMethod.Get, pageUrl), ct);
            Ensure(response);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("value", out var values))
                foreach (var m in values.EnumerateArray())
                    if (Str(m, "id") is { } id && ReweInvoice.IsInvoiceSubject(Str(m, "subject"))) ids.Add(id);
            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
        return ids;
    }

    /// <summary>The invoice PDFs attached to a mail (attachments named "Rechnung_PN….pdf"; nothing else is downloaded).</summary>
    private async Task<List<byte[]>> InvoicePdfsAsync(string userId, string messageId, CancellationToken ct)
    {
        var baseUrl = $"{_graph.GraphBase}/me/messages/{Uri.EscapeDataString(messageId)}/attachments";
        var list = new List<(string Id, long? Size)>();
        using (var response = await _graph.GraphRequestAsync(userId, () => new HttpRequestMessage(HttpMethod.Get, baseUrl + "?$select=id,name,size"), ct))
        {
            Ensure(response);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("value", out var values))
                foreach (var a in values.EnumerateArray())
                    if (Str(a, "id") is { } id && ReweInvoice.IsInvoiceAttachment(Str(a, "name")))
                        list.Add((id, a.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : null));
        }
        var pdfs = new List<byte[]>();
        foreach (var (id, size) in list)
        {
            if (size > _options.MaxAttachmentBytes) continue;
            var url = $"{baseUrl}/{Uri.EscapeDataString(id)}/$value";
            using var response = await _graph.GraphRequestAsync(userId, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
            Ensure(response);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length <= _options.MaxAttachmentBytes) pdfs.Add(bytes);
        }
        return pdfs;
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new CloudException((int)response.StatusCode switch
        {
            403 => "Microsoft refused reading the mailbox. Grant mail access again; the server's app registration needs the Mail.Read permission.",
            429 => "Microsoft is throttling requests; the mailbox is read again later.",
            _ => $"Microsoft Graph answered with HTTP {(int)response.StatusCode} while reading the mailbox.",
        });
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public override void Dispose()
    {
        lock (_lock)
        {
            foreach (var g in _gates.Values) g.Dispose();
            _gates.Clear();
        }
        base.Dispose();
    }
}
