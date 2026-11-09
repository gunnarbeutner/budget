using System.Globalization;
using System.Text.Json;

namespace OpenBudget.Connectors.FinTs;

/// <summary>A TAN method (security function) a bank offers, from the HITANS parameter segments.</summary>
public sealed record FinTsTanMethod(
    string Id,
    string Name,
    int HktanVersion,
    bool Decoupled,
    int? MaxInputLength,
    bool Numeric,
    int MaxPolls,
    TimeSpan WaitFirst,
    TimeSpan WaitNext,
    string TechnicalId)
{
    /// <summary>Whether this client can drive the method (HKTAN version 6 or 7).</summary>
    public bool Supported => HktanVersion is 6 or 7;
}

/// <summary>An account from the user parameter data (UPD).</summary>
public sealed record FinTsAccountInfo(
    string AccountNumber,
    string SubNumber,
    string Blz,
    string? Iban,
    string? Bic,
    string CustomerId,
    int? AccountType,
    string Currency,
    string Name,
    string Product,
    IReadOnlySet<string> AllowedJobs,
    string? Inventory)
{
    /// <summary>Stable id across logins: the IBAN, else the national account number.</summary>
    public string RemoteId => !string.IsNullOrEmpty(Iban) ? Iban : AccountNumber + (SubNumber.Length > 0 ? "-" + SubNumber : string.Empty);

    public bool Allows(string job) => AllowedJobs.Count == 0 || AllowedJobs.Contains(job);

    public RemoteAccountType Kind => AccountType switch
    {
        >= 1 and <= 9 => RemoteAccountType.Giro,
        >= 10 and <= 29 => RemoteAccountType.Savings,
        >= 30 and <= 39 => RemoteAccountType.Depot,
        >= 50 and <= 59 => RemoteAccountType.CreditCard,
        >= 60 and <= 69 => RemoteAccountType.Depot,
        null when AllowedJobs.Contains("HKWPD") && !AllowedJobs.Contains("HKKAZ") && !AllowedJobs.Contains("HKCAZ") => RemoteAccountType.Depot,
        null => LooksLikeSavings(Product) ? RemoteAccountType.Savings : RemoteAccountType.Giro,
        _ => RemoteAccountType.Other,
    };

    private static bool LooksLikeSavings(string product) =>
        product.Contains("Extra", StringComparison.OrdinalIgnoreCase)
        || product.Contains("Spar", StringComparison.OrdinalIgnoreCase)
        || product.Contains("Tagesgeld", StringComparison.OrdinalIgnoreCase)
        || product.Contains("Festgeld", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The bank parameter data (BPD) and user parameter data (UPD) a bank sent, kept as the segments themselves
/// so they can be cached in the session and parsed again.
/// </summary>
public sealed class FinTsBankData
{
    private static readonly HashSet<string> StopNames = new(StringComparer.Ordinal)
    {
        "HIUPA", "HIUPD", "HITAN", "HIRMG", "HIRMS", "HNSHA", "HNHBS",
    };

    private FinTsBankData(IReadOnlyList<FinTsSegment> bpd, IReadOnlyList<FinTsSegment> upd)
    {
        BpdSegments = bpd;
        UpdSegments = upd;
        Methods = ParseMethods(bpd);
        Accounts = ParseAccounts(upd);
        PinRequirements = ParsePinRequirements(bpd);
    }

    public static FinTsBankData Empty { get; } = new([], []);

    public IReadOnlyList<FinTsSegment> BpdSegments { get; }

    public IReadOnlyList<FinTsSegment> UpdSegments { get; }

    public IReadOnlyList<FinTsTanMethod> Methods { get; }

    public IReadOnlyList<FinTsAccountInfo> Accounts { get; }

    /// <summary>Per business transaction code (HKSAL, ...): does the bank want a TAN process (HKTAN) with it?</summary>
    public IReadOnlyDictionary<string, bool> PinRequirements { get; }

    public int BpdVersion => BpdSegments.FirstOrDefault(s => string.Equals(s.Name, "HIBPA", StringComparison.Ordinal)) is { } s
        && int.TryParse(s.Element(0), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public int UpdVersion => UpdSegments.FirstOrDefault(s => string.Equals(s.Name, "HIUPA", StringComparison.Ordinal)) is { } s
        && int.TryParse(s.Element(1), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public bool HasBpd => BpdSegments.Count > 0;

    public static FinTsBankData Create(IReadOnlyList<FinTsSegment> bpd, IReadOnlyList<FinTsSegment> upd) => new(bpd, upd);

    public FinTsBankData WithBpd(IReadOnlyList<FinTsSegment> bpd) => new(bpd, UpdSegments);

    public FinTsBankData WithUpd(IReadOnlyList<FinTsSegment> upd) => new(BpdSegments, upd);

    /// <summary>The BPD part of a response: from HIBPA up to the user data or the first non-parameter segment.</summary>
    public static List<FinTsSegment>? ExtractBpd(IReadOnlyList<FinTsSegment> segments)
    {
        var start = -1;
        for (var i = 0; i < segments.Count; i++)
        {
            if (string.Equals(segments[i].Name, "HIBPA", StringComparison.Ordinal)) { start = i; break; }
        }
        if (start < 0) return null;
        var result = new List<FinTsSegment>();
        for (var i = start; i < segments.Count && !StopNames.Contains(segments[i].Name); i++) result.Add(segments[i]);
        return result;
    }

    /// <summary>The UPD part: HIUPA and the HIUPD segments following it.</summary>
    public static List<FinTsSegment>? ExtractUpd(IReadOnlyList<FinTsSegment> segments)
    {
        if (!segments.Any(s => string.Equals(s.Name, "HIUPA", StringComparison.Ordinal))) return null;
        return segments.Where(s => s.Name is "HIUPA" or "HIUPD").ToList();
    }

    /// <summary>Highest version of the parameter segment <c>HI&lt;job&gt;S</c> that is also in <paramref name="supported"/>; null when the bank offers none.</summary>
    public int? BestVersion(string job, IReadOnlyCollection<int> supported)
    {
        var parameterName = "HI" + job[2..] + "S";
        int? best = null;
        foreach (var s in BpdSegments)
        {
            if (string.Equals(s.Name, parameterName, StringComparison.Ordinal) && supported.Contains(s.Version) && (best == null || s.Version > best)) best = s.Version;
        }
        return best;
    }

    public FinTsSegment? ParameterSegment(string job, int version)
    {
        var parameterName = "HI" + job[2..] + "S";
        return BpdSegments.FirstOrDefault(s => string.Equals(s.Name, parameterName, StringComparison.Ordinal) && s.Version == version);
    }

    public bool NeedsTan(string job) => PinRequirements.TryGetValue(job, out var needed) && needed;

    private static List<FinTsTanMethod> ParseMethods(IReadOnlyList<FinTsSegment> bpd)
    {
        var methods = new List<FinTsTanMethod>();
        foreach (var segment in bpd.Where(s => string.Equals(s.Name, "HITANS", StringComparison.Ordinal)))
        {
            var chunk = segment.Version switch { 6 => 21, 7 => 26, _ => 0 };
            if (chunk == 0) continue;
            var parameters = segment.Deg(3);
            // Three leading flags (one-step allowed, several TANs per message, job hash mode), then the methods back to back.
            for (var at = 3; at + 6 < parameters.Count; at += chunk)
            {
                string Part(int i) => at + i < parameters.Count ? parameters[at + i] : string.Empty;
                int Number(int i, int fallback) => int.TryParse(Part(i), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : fallback;
                var id = Part(0);
                if (id.Length == 0) continue;
                var decoupled = Part(3).Contains("Decoupled", StringComparison.OrdinalIgnoreCase);
                var name = Part(5).Length > 0 ? Part(5) : Part(2);
                methods.Add(new FinTsTanMethod(
                    id,
                    name.Length > 0 ? name : "TAN method " + id,
                    segment.Version,
                    decoupled,
                    int.TryParse(Part(6), NumberStyles.None, CultureInfo.InvariantCulture, out var max) && max > 0 ? max : null,
                    string.Equals(Part(7), "1", StringComparison.Ordinal),
                    segment.Version == 7 ? Number(21, 999) : 0,
                    TimeSpan.FromSeconds(segment.Version == 7 ? Number(22, 0) : 0),
                    TimeSpan.FromSeconds(segment.Version == 7 ? Number(23, 0) : 0),
                    Part(2)));
            }
        }
        return methods;
    }

    private static Dictionary<string, bool> ParsePinRequirements(IReadOnlyList<FinTsSegment> bpd)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        var pins = bpd.FirstOrDefault(s => string.Equals(s.Name, "HIPINS", StringComparison.Ordinal));
        if (pins == null) return result;
        var parameters = pins.Deg(3);
        for (var i = 5; i + 1 < parameters.Count; i += 2) result[parameters[i]] = string.Equals(parameters[i + 1], "J", StringComparison.Ordinal);
        return result;
    }

    private static List<FinTsAccountInfo> ParseAccounts(IReadOnlyList<FinTsSegment> upd)
    {
        var accounts = new List<FinTsAccountInfo>();
        foreach (var s in upd.Where(s => string.Equals(s.Name, "HIUPD", StringComparison.Ordinal)))
        {
            var number = s.Component(0, 0);
            if (number.Length == 0) continue;
            var jobs = new HashSet<string>(StringComparer.Ordinal);
            string? extension = null;
            for (var i = 9; i < s.Elements.Count; i++)
            {
                var e = s.Elements[i];
                if (e.Count == 0 || e[0].Length == 0) continue;
                if (e[0].StartsWith('{', StringComparison.Ordinal)) extension = string.Join(':', e);
                else if (e[0].Length is >= 5 and <= 6 && char.IsUpper(e[0][0])) jobs.Add(e[0]);
            }
            var json = ParseExtension(extension);
            accounts.Add(new FinTsAccountInfo(
                number,
                s.Component(0, 1),
                s.Component(0, 3),
                s.Element(1) is { Length: > 0 } iban ? iban : null,
                json.Bic,
                s.Element(2),
                int.TryParse(s.Element(3), NumberStyles.None, CultureInfo.InvariantCulture, out var type) ? type : null,
                s.Element(4) is { Length: > 0 } cur ? cur : "EUR",
                string.Join(' ', new[] { s.Element(5), s.Element(6) }.Where(n => n.Length > 0)),
                s.Element(7),
                jobs,
                json.Inventory));
        }
        return accounts;
    }

    private static (string? Bic, string? Inventory) ParseExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(extension);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            string? bic = null, inventory = null;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) continue;
                if (string.Equals(p.Name, "BIC", StringComparison.OrdinalIgnoreCase)) bic = p.Value.GetString();
                else if (string.Equals(p.Name, "umsltzt", StringComparison.OrdinalIgnoreCase)) inventory = p.Value.GetString();
            }
            return (bic, inventory);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
