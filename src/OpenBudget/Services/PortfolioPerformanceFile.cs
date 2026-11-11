using System.IO.Compression;
using OpenBudget.Models;
using OpenBudget.PortfolioPerformance.Proto;

namespace OpenBudget.Services;

/// <summary>
/// Reads a PortfolioPerformance ".portfolio" file and flattens its taxonomy assignments into
/// <see cref="SecurityTaxonomy"/> rows. Pure (no DB) so it's unit-testable against a real file.
///
/// File shape: a ZIP whose single "data.portfolio" entry is the 6-byte ASCII signature "PPPBV1"
/// followed immediately by a serialized <see cref="PClient"/>. Only the unencrypted binary format
/// is supported; an XML or AES-encrypted save throws <see cref="InvalidDataException"/>.
/// </summary>
public static class PortfolioPerformanceFile
{
    private static readonly byte[] Signature = "PPPBV1"u8.ToArray();

    /// <summary>Parses the PClient out of an open .portfolio file stream.</summary>
    public static PClient ReadClient(Stream portfolioFile)
    {
        using var archive = new ZipArchive(portfolioFile, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry("data.portfolio")
            ?? throw new InvalidDataException("Not a PortfolioPerformance file: no 'data.portfolio' entry in the archive.");

        byte[] bytes;
        using (var entryStream = entry.Open())
        using (var buffer = new MemoryStream())
        {
            entryStream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        if (bytes.Length < Signature.Length || !bytes.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException(
                "Unsupported PortfolioPerformance format: expected the 'PPPBV1' binary signature. " +
                "XML and password-encrypted files are not supported — save as the default binary format without encryption.");
        }

        return PClient.Parser.ParseFrom(new ReadOnlySpan<byte>(bytes, Signature.Length, bytes.Length - Signature.Length));
    }

    /// <summary>
    /// Flattens every taxonomy assignment that targets an ISIN-bearing security into rows for the
    /// given source. Assignments to accounts, portfolios, or securities without an ISIN (e.g.
    /// crypto) are skipped — they can't join to <see cref="Holding"/>.
    /// </summary>
    public static List<SecurityTaxonomy> Flatten(PClient client, int sourceId)
    {
        // uuid → ISIN for securities that carry one. Indexer (not ToDictionary) so a duplicate
        // uuid — never seen in practice — degrades to last-wins instead of throwing mid-import.
        var uuidToIsin = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in client.Securities)
        {
            if (!string.IsNullOrEmpty(s.Uuid) && !string.IsNullOrEmpty(s.Isin))
                uuidToIsin[s.Uuid] = s.Isin;
        }

        var rows = new List<SecurityTaxonomy>();
        foreach (var taxonomy in client.Taxonomies)
        {
            var byId = new Dictionary<string, PTaxonomy.Types.Classification>(StringComparer.Ordinal);
            foreach (var c in taxonomy.Classifications) byId[c.Id] = c;

            foreach (var classification in taxonomy.Classifications)
            {
                if (classification.Assignments.Count == 0) continue;
                var path = BuildPath(classification, byId);
                foreach (var assignment in classification.Assignments)
                {
                    if (!uuidToIsin.TryGetValue(assignment.InvestmentVehicle, out var isin)) continue;
                    rows.Add(new SecurityTaxonomy
                    {
                        SourceId = sourceId,
                        Isin = isin,
                        TaxonomyId = taxonomy.Id,
                        TaxonomyName = taxonomy.Name,
                        ClassificationId = classification.Id,
                        ClassificationName = classification.Name,
                        ClassificationPath = path,
                        WeightBp = assignment.Weight
                    });
                }
            }
        }
        return rows;
    }

    /// <summary>
    /// Every catalogued security as a <see cref="PpSecurity"/> row for the given source, keyed by
    /// uuid — including the ISIN-less instruments (crypto, real estate) that join to the source app by a
    /// manual mapping rather than by ISIN. A security with neither uuid nor a usable name is skipped.
    /// </summary>
    public static List<PpSecurity> ExtractSecurities(PClient client, int sourceId)
    {
        var byUuid = new Dictionary<string, PpSecurity>(StringComparer.Ordinal);
        foreach (var s in client.Securities)
        {
            if (string.IsNullOrEmpty(s.Uuid)) continue;
            // Latest quote, ×1e8; absent or zero ⇒ no price.
            decimal? price = s.Latest is { Close: > 0 } latest ? latest.Close / 100_000_000m : null;
            byUuid[s.Uuid] = new PpSecurity
            {
                SourceId = sourceId,
                Uuid = s.Uuid,
                Isin = s.Isin ?? "",
                Name = string.IsNullOrEmpty(s.Name) ? (string.IsNullOrEmpty(s.Isin) ? s.Uuid : s.Isin) : s.Name,
                TickerSymbol = string.IsNullOrEmpty(s.TickerSymbol) ? null : s.TickerSymbol,
                Wkn = string.IsNullOrEmpty(s.Wkn) ? null : s.Wkn,
                Price = price,
                Currency = price is null || string.IsNullOrEmpty(s.CurrencyCode) ? null : s.CurrencyCode
            };
        }
        return byUuid.Values.ToList();
    }

    /// <summary>
    /// The depots (portfolios) catalogued in the file, as <see cref="PpPortfolio"/> rows for the
    /// given source — the candidates a user maps to their imported depot accounts.
    /// </summary>
    public static List<PpPortfolio> ExtractPortfolios(PClient client, int sourceId) =>
        client.Portfolios
            .Where(p => !string.IsNullOrEmpty(p.Uuid))
            .Select(p => new PpPortfolio { SourceId = sourceId, Uuid = p.Uuid, Name = string.IsNullOrEmpty(p.Name) ? p.Uuid : p.Name, IsRetired = p.IsRetired })
            .ToList();

    /// <summary>
    /// Names from the taxonomy's top level down to <paramref name="classification"/>, " / "-joined.
    /// The synthetic root (the node with no parent) is excluded so paths read as "Equity / North
    /// America" rather than "Asset Classes / Equity / North America".
    /// </summary>
    private static string BuildPath(
        PTaxonomy.Types.Classification classification,
        Dictionary<string, PTaxonomy.Types.Classification> byId)
    {
        var names = new List<string>();
        var current = classification;
        // Stop once we reach a node whose parent is empty (the root) — it's added by the loop's
        // predecessor, not itself, so the root name never enters the list.
        var guard = 0;
        while (current != null && !string.IsNullOrEmpty(current.ParentId) && guard++ < 64)
        {
            names.Add(current.Name);
            byId.TryGetValue(current.ParentId, out current);
        }
        names.Reverse();
        return string.Join(" / ", names);
    }
}
