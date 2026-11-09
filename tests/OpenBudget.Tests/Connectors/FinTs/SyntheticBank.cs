using System.Globalization;
using System.Text;
using OpenBudget.Connectors.FinTs;

namespace OpenBudget.Tests.Connectors.FinTs;

internal enum TanMode
{
    /// <summary>No TAN step at all (SCA exempt).</summary>
    None,

    /// <summary>The bank answers 3955 and wants the user to approve in an app; <see cref="SyntheticBank.PendingPolls"/> polls say 3956 first.</summary>
    Decoupled,

    /// <summary>The bank answers 0030 and wants <see cref="SyntheticBank.TanCode"/>.</summary>
    Code,
}

/// <summary>
/// A small bank that follows the protocol rules the client relies on: PIN check, TAN method negotiation (3920),
/// parameter data on init, customer system id on HKSYN, TAN processes 4/S/2, touchdown paging, dialog end.
/// Everything it serves is made up.
/// </summary>
internal sealed class SyntheticBank
{
    public const string Account = "1234567";
    public const string Iban = "DE00000000000000000011";
    public const string DepotAccount = "7654321";
    public const string AssignedSystemId = "SYSID0001";
    public const string Blz = "12345678";

    private readonly Dictionary<string, Dialog> _dialogs = new(StringComparer.Ordinal);
    private int _dialogCounter;

    public string UserId { get; set; } = "testuser";

    public string Pin { get; set; } = "12345";

    public List<string> AllowedMethods { get; set; } = ["940"];

    public string BpdText { get; set; } = DefaultBpd;

    public string UpdText { get; set; } = DefaultUpd;

    public TanMode InitTan { get; set; }

    public TanMode JobTan { get; set; }

    public int PendingPolls { get; set; } = 2;

    public bool NeverApprove { get; set; }

    public string TanCode { get; set; } = "654321";

    /// <summary>Jobs the bank does not know (answered with an error).</summary>
    public HashSet<string> Unsupported { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Action<FinTsSegment, ReplyBuilder, MockRequest>> Handlers { get; } = new(StringComparer.Ordinal);

    public int SyncCount { get; private set; }

    public int InitCount { get; private set; }

    public int PollCount { get; private set; }

    public static string DefaultBpd =>
        "HIBPA:3:3:4+7+280:12345678+Test Bank+0+1+300+0'"
        + "HIPINS:4:1:4+1+1+0+5:64:6:Alias::HKCAZ:J:HKSAL:J:HKKAZ:J:HKWPD:J:HKEKA:J'"
        + "HITANS:5:7:4+1+1+0+N:N:0:" + MethodV7("940", "Test App", decoupled: true) + "'"
        + "HITANS:6:6:4+1+1+0+N:N:0:" + MethodV6("930", "smsTAN") + ":" + MethodV6("931", "chipTAN") + "'"
        + "HISALS:7:7:4+1+1+0'HISALS:8:5:4+1+1+0'"
        + "HICAZS:9:1:4+1+1+0+390:N:N:urn?:iso?:std?:iso?:20022?:tech?:xsd?:camt.052.001.02'"
        + "HIKAZS:10:7:4+1+1+0+390:N:N'"
        + "HIWPDS:11:6:4+1+1+0+N:N:N'"
        + "HIEKAS:12:5:4+1+1+0+N:N:N:3'";

    public static string DefaultUpd =>
        "HIUPA:13:4:4+testuser+5+0'"
        + $"HIUPD:14:6:4+{Account}::280:{Blz}+{Iban}+testuser+1+EUR+TEST OWNER++Girokonto++HKSAL:1+HKKAZ:1+HKCAZ:1+HKEKA:1'"
        + $"HIUPD:15:6:4+{DepotAccount}::280:{Blz}++testuser+30+EUR+TEST OWNER++Depot++HKWPD:1'";

    public static string MethodV7(string id, string name, bool decoupled, int maxPolls = 999) =>
        $"{id}:2:{name}:{(decoupled ? "Decoupled" : "HHD")}::{name}:::{name}:999:N:1:N:0:2:N:J:00:0:N::{maxPolls}:0:0";

    public static string MethodV6(string id, string name) =>
        $"{id}:2:{name}:mobileTAN::{name}:6:1:mobileTAN:999:N:1:N:0:2:N:J:00:0:N:";

    public string Respond(MockRequest request)
    {
        var rb = new ReplyBuilder();
        if (!string.Equals(request.UserId, UserId, StringComparison.Ordinal) || !string.Equals(request.Pin, Pin, StringComparison.Ordinal))
            return rb.Global("9931::Anmeldename oder PIN falsch.").Wrap("0", request.MessageNumber);

        var init = request.Segments.FirstOrDefault(s => s.Name == "HKIDN");
        Dialog dialog;
        if (init != null)
        {
            if (!AllowedMethods.Contains(request.SecurityFunction))
                return rb.Global("9955::Ungültiges TAN-Verfahren.").Raw(FinTsWire.Parse(BpdText)).Codes(init.Number, "3920::Zugelassene TAN-Verfahren:" + string.Join(':', AllowedMethods)).Wrap("0", request.MessageNumber);
            dialog = new Dialog($"DLG{++_dialogCounter}");
            _dialogs[dialog.Id] = dialog;
        }
        else if (!_dialogs.TryGetValue(request.DialogId, out dialog!))
        {
            return rb.Global("9800::Dialog abgebrochen.").Wrap("0", request.MessageNumber);
        }

        var tan = request.Segments.FirstOrDefault(s => s.Name == "HKTAN");
        if (tan != null && tan.Element(0) == "S") return Poll(request, rb, dialog, tan);
        if (tan != null && tan.Element(0) == "2") return EnterTan(request, rb, dialog, tan);

        if (request.Segments.Any(s => s.Name == "HKEND"))
        {
            _dialogs.Remove(dialog.Id);
            return rb.Global("0100::Dialog beendet.").Wrap(dialog.Id, request.MessageNumber);
        }

        void Fill(ReplyBuilder target)
        {
            target.Global("0010::Nachricht entgegengenommen.");
            foreach (var segment in request.Segments.Where(s => s.Name is not ("HKTAN")))
                Execute(request, segment, target);
        }

        var mode = init != null ? InitTan : JobTan;
        if (tan != null && mode != TanMode.None)
        {
            dialog.Pending = Fill;
            dialog.Polls = 0;
            return Challenge(request, rb, dialog, tan, mode);
        }
        Fill(rb);
        if (tan != null) rb.Codes(tan.Number, "3076::Starke Kundenauthentifizierung nicht notwendig.").Segment("HITAN", 6, tan.Number, "4++noref+nochallenge");
        return rb.Wrap(dialog.Id, request.MessageNumber);
    }

    private string Challenge(MockRequest request, ReplyBuilder rb, Dialog dialog, FinTsSegment tan, TanMode mode)
    {
        rb.Global("0010::Nachricht entgegengenommen.");
        if (mode == TanMode.Decoupled)
            rb.Codes(tan.Number, "3955::Sicherheitsfreigabe über anderen Kanal erforderlich.").Segment("HITAN", 7, tan.Number, "4++ORDER1+Bitte in der App bestätigen");
        else
            rb.Codes(tan.Number, "0030::Auftrag empfangen - Sicherheitsfreigabe erforderlich.").Segment("HITAN", 6, tan.Number, "4++ORDER1+Bitte die TAN eingeben<br>Danke");
        return rb.Wrap(dialog.Id, request.MessageNumber);
    }

    private string Poll(MockRequest request, ReplyBuilder rb, Dialog dialog, FinTsSegment tan)
    {
        PollCount++;
        if (dialog.Pending == null) return rb.Global("9050::Kein Auftrag.").Wrap(dialog.Id, request.MessageNumber);
        if (NeverApprove || ++dialog.Polls <= PendingPolls)
        {
            rb.Global("0010::Nachricht entgegengenommen.").Codes(tan.Number, "3956::Starke Kundenauthentifizierung noch ausstehend.").Segment("HITAN", 7, tan.Number, "S++ORDER1");
            return rb.Wrap(dialog.Id, request.MessageNumber);
        }
        dialog.Pending(rb);
        dialog.Pending = null;
        rb.Codes(tan.Number, "0020::Auftrag ausgeführt.");
        return rb.Wrap(dialog.Id, request.MessageNumber);
    }

    private string EnterTan(MockRequest request, ReplyBuilder rb, Dialog dialog, FinTsSegment tan)
    {
        if (dialog.Pending == null || request.Tan != TanCode)
            return rb.Global("9941::TAN ungültig.").Wrap(dialog.Id, request.MessageNumber);
        dialog.Pending(rb);
        dialog.Pending = null;
        rb.Codes(tan.Number, "0020::Auftrag ausgeführt.");
        return rb.Wrap(dialog.Id, request.MessageNumber);
    }

    private void Execute(MockRequest request, FinTsSegment s, ReplyBuilder rb)
    {
        switch (s.Name)
        {
            case "HKIDN":
                InitCount++;
                rb.Codes(s.Number, "0020::Angemeldet.", "3920::Zugelassene Verfahren:" + string.Join(':', AllowedMethods));
                break;
            case "HKVVB":
                var bpdVersion = FinTsWire.Parse(BpdText).First(x => x.Name == "HIBPA").Element(0);
                if (s.Element(0) != bpdVersion) rb.Raw(FinTsWire.Parse(BpdText));
                var upd = FinTsWire.Parse(UpdText);
                var updVersion = upd.First(x => x.Name == "HIUPA").Element(1);
                if (s.Element(1) != updVersion || s.Element(1) == "0") rb.Raw(upd);
                rb.Codes(s.Number, "0020::Dialoginitialisierung erfolgreich");
                break;
            case "HKSYN":
                SyncCount++;
                rb.Segment("HISYN", 4, s.Number, AssignedSystemId);
                rb.Codes(s.Number, "0020::Synchronisierung erfolgreich");
                break;
            default:
                if (Unsupported.Contains(s.Name)) rb.Codes(s.Number, $"9120::Auftrag {s.Name} nicht unterstützt.");
                else if (Handlers.TryGetValue(s.Name, out var handler)) handler(s, rb, request);
                else rb.Codes(s.Number, "9120::Unbekannter Auftrag.");
                break;
        }
    }

    private sealed class Dialog
    {
        public Dialog(string id) => Id = id;

        public string Id { get; }

        public Action<ReplyBuilder>? Pending { get; set; }

        public int Polls { get; set; }
    }

    // ---------------------------------------------------------------- content helpers

    public static string Camt(params (string Status, string Direction, string Amount, string Date, string Name, string Purpose, string? Ref)[] entries)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:camt.052.001.02\"><BkToCstmrAcctRpt><Rpt><Id>R</Id>");
        foreach (var e in entries)
        {
            var party = e.Direction == "CRDT" ? "Dbtr" : "Cdtr";
            sb.Append(CultureInfo.InvariantCulture, $"<Ntry><Amt Ccy=\"EUR\">{e.Amount}</Amt><CdtDbtInd>{e.Direction}</CdtDbtInd><Sts>{e.Status}</Sts><BookgDt><Dt>{e.Date}</Dt></BookgDt><ValDt><Dt>{e.Date}</Dt></ValDt>");
            if (e.Ref != null) sb.Append(CultureInfo.InvariantCulture, $"<AcctSvcrRef>{e.Ref}</AcctSvcrRef>");
            sb.Append(CultureInfo.InvariantCulture, $"<NtryDtls><TxDtls><RltdPties><{party}><Nm>{e.Name}</Nm></{party}></RltdPties><RmtInf><Ustrd>{e.Purpose}</Ustrd></RmtInf></TxDtls></NtryDtls></Ntry>");
        }
        return sb.Append("</Rpt></BkToCstmrAcctRpt></Document>").ToString();
    }

    public static string Bin(string data) => FinTsWire.Binary(data);

    /// <summary>Serves a booked balance of <paramref name="amount"/> (HISAL v7).</summary>
    public void UseBalance(string amount = "1234,56") =>
        Handlers["HKSAL"] = (s, rb, _) => rb.Segment("HISAL", 7, s.Number, $"{Iban}::{Account}::280:{Blz}", "Girokonto", "EUR", $"C:{amount}:EUR:20261109");

    /// <summary>Serves one booked and one pending camt entry for HKCAZ.</summary>
    public void UseCamt() =>
        Handlers["HKCAZ"] = (s, rb, _) => rb.Segment(
            "HICAZ", 1, s.Number,
            $"{Iban}::{Account}::280:{Blz}",
            "urn?:iso?:std?:iso?:20022?:tech?:xsd?:camt.052.001.02",
            Bin(Camt(("BOOK", "DBIT", "12.30", "2026-11-03", "Example Shop", "Purchase", "REF1"))),
            Bin(Camt(("PDNG", "DBIT", "4.00", "2026-11-09", "Example Cafe", "Coffee", null))));
}
