using System.Text;

namespace OpenBudget.Connectors;

/// <summary>
/// Repairs bank texts whose UTF-8 bytes were read as ISO-8859-1 / Windows-1252 ("Ãœberweisung" for "Überweisung").
/// A text is only changed when mapping every character back to its single byte gives valid UTF-8 that has fewer
/// non-ASCII characters, so ordinary text with "Ã" or other Latin-1 letters stays as it is.
/// </summary>
public static class BankText
{
    private static readonly Encoding Cp1252 = CodePage1252();
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static Encoding CodePage1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static string? Repair(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Lead bytes of multi-byte UTF-8 sequences, read as Latin-1, are Â..ô (C2..F4).
        if (!text.Any(c => c is >= 'Â' and <= 'ô')) return text;
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c <= 'ÿ')
            {
                bytes[i] = (byte)c;
                continue;
            }
            // Windows-1252 shows the bytes 0x80..0x9F as letters such as "œ" or "€"; any other character cannot come from one byte.
            try
            {
                var one = Cp1252.GetBytes([c]);
                if (one.Length != 1) return text;
                bytes[i] = one[0];
            }
            catch (EncoderFallbackException)
            {
                return text;
            }
        }
        try
        {
            var decoded = StrictUtf8.GetString(bytes);
            return NonAscii(decoded) < NonAscii(text) ? decoded : text;
        }
        catch (DecoderFallbackException)
        {
            return text;
        }
    }

    /// <summary>The transaction with its name, purpose and booking text repaired.</summary>
    public static RemoteTransaction Repair(RemoteTransaction t)
    {
        var repaired = t with { Name = Repair(t.Name), Purpose = Repair(t.Purpose), KeyPurpose = Repair(t.KeyPurpose), BookingText = Repair(t.BookingText) };
        return repaired == t ? t : repaired;
    }

    private static int NonAscii(string s) => s.Count(c => c > '\u007F');
}
