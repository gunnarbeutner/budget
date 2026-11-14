using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace OpenBudget.Connectors.Receipts;

/// <summary>The text of a PDF as lines, page by page, read with PdfPig.</summary>
public static class ReceiptPdf
{
    /// <summary>Words whose baselines are this close (in points) are on one line.</summary>
    private const double LineTolerance = 2.0;

    /// <summary>
    /// The lines of every page, top to bottom; a line's words left to right, joined by single spaces. No-break and other
    /// unusual spaces become plain spaces. Throws <see cref="InvalidDataException"/> for something that is not a readable PDF.
    /// </summary>
    public static List<string> Lines(byte[] pdf)
    {
        try
        {
            using var document = PdfDocument.Open(pdf);
            var lines = new List<string>();
            foreach (var page in document.GetPages()) lines.AddRange(PageLines(page));
            return lines;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not InvalidDataException)
        {
            throw new InvalidDataException("The PDF could not be read.", ex);
        }
    }

    private static IEnumerable<string> PageLines(Page page)
    {
        var rows = new List<(double Y, List<Word> Words)>();
        foreach (var word in page.GetWords().OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var y = word.BoundingBox.Bottom;
            var row = rows.FindIndex(r => Math.Abs(r.Y - y) <= LineTolerance);
            if (row < 0) rows.Add((y, [word]));
            else rows[row].Words.Add(word);
        }
        foreach (var (_, words) in rows.OrderByDescending(r => r.Y))
        {
            var text = Normalize(string.Join(' ', words.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
            if (text.Length > 0) yield return text;
        }
    }

    /// <summary>Unusual spaces become plain ones, runs of spaces one, ends trimmed.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c == ' ' || c == ' ')
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
