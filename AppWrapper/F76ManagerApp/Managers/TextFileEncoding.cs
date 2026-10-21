using System.Runtime.InteropServices;
using System.Text;

namespace F76ManagerApp.Managers;

public static class TextFileEncoding
{
    public const string Utf8 = "utf-8";
    public const string Utf8Bom = "utf-8-bom";
    public const string Utf16Le = "utf-16le";
    public const string Utf16Be = "utf-16be";
    public const string Utf32Le = "utf-32le";
    private const string CodePagePrefix = "cp:";

    static TextFileEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [DllImport("kernel32.dll")]
    private static extern int GetACP();

    public static string Read(string path, out string token)
    {
        byte[] bytes = File.ReadAllBytes(path);
        token = Detect(bytes, out int bomLength);
        return GetEncoding(token, strictEncode: false).GetString(bytes, bomLength, bytes.Length - bomLength);
    }

    public static string Detect(byte[] bytes, out int bomLength)
    {
        bomLength = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { bomLength = 3; return Utf8Bom; }
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0) { bomLength = 4; return Utf32Le; }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { bomLength = 2; return Utf16Le; }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { bomLength = 2; return Utf16Be; }

        try
        {
            new UTF8Encoding(false, true).GetCharCount(bytes);
            return Utf8;
        }
        catch (DecoderFallbackException)
        {
        }

        foreach (int cp in LegacyCodePageCandidates())
        {
            if (RoundTrips(cp, bytes)) return CodePagePrefix + cp;
        }
        return CodePagePrefix + Latin1CodePage;
    }

    private const int Latin1CodePage = 28591;

    private static IEnumerable<int> LegacyCodePageCandidates()
    {
        var seen = new HashSet<int>();
        int acp;
        try { acp = GetACP(); }
        catch { acp = 0; }
        foreach (int cp in new[]
        {
            acp,
            System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage,
            System.Globalization.CultureInfo.CurrentUICulture.TextInfo.ANSICodePage,
        })
        {
            if (cp <= 0 || cp == 65001 || !seen.Add(cp) || !TryGetCodePage(cp, out _)) continue;
            yield return cp;
        }
    }

    private static bool RoundTrips(int codePage, byte[] bytes)
    {
        try
        {
            var enc = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return enc.GetBytes(enc.GetString(bytes)).AsSpan().SequenceEqual(bytes);
        }
        catch
        {
            return false;
        }
    }

    public static string Normalize(string? token)
    {
        string t = (token ?? "").Trim().ToLowerInvariant();
        switch (t)
        {
            case Utf8:
            case Utf8Bom:
            case Utf16Le:
            case Utf16Be:
            case Utf32Le:
                return t;
        }
        if (t.StartsWith(CodePagePrefix, StringComparison.Ordinal) &&
            int.TryParse(t.AsSpan(CodePagePrefix.Length), out int cp) && TryGetCodePage(cp, out _))
        {
            return CodePagePrefix + cp;
        }
        return Utf8;
    }

    public static Encoding GetEncoding(string token, bool strictEncode)
    {
        switch (Normalize(token))
        {
            case Utf8Bom: return new UTF8Encoding(true);
            case Utf16Le: return new UnicodeEncoding(false, true);
            case Utf16Be: return new UnicodeEncoding(true, true);
            case Utf32Le: return new UTF32Encoding(false, true);
            case Utf8: return new UTF8Encoding(false);
        }
        int cp = int.Parse(Normalize(token).AsSpan(CodePagePrefix.Length));
        return Encoding.GetEncoding(cp,
            strictEncode ? EncoderFallback.ExceptionFallback : EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
    }

    public static string Label(string token)
    {
        string t = Normalize(token);
        switch (t)
        {
            case Utf8: return "UTF-8";
            case Utf8Bom: return "UTF-8 BOM";
            case Utf16Le: return "UTF-16 LE";
            case Utf16Be: return "UTF-16 BE";
            case Utf32Le: return "UTF-32 LE";
        }
        int cp = int.Parse(t.AsSpan(CodePagePrefix.Length));
        return cp switch
        {
            932 => "Shift-JIS",
            936 => "GBK",
            949 => "EUC-KR",
            950 => "Big5",
            874 => "Windows-874",
            Latin1CodePage => "ISO-8859-1",
            >= 1250 and <= 1258 => $"Windows-{cp}",
            _ => TryGetCodePage(cp, out var enc) ? enc!.WebName.ToUpperInvariant() : $"CP{cp}"
        };
    }

    public static bool IsCodePage(string token) =>
        Normalize(token).StartsWith(CodePagePrefix, StringComparison.Ordinal);

    private static bool TryGetCodePage(int cp, out Encoding? encoding)
    {
        try
        {
            encoding = Encoding.GetEncoding(cp);
            return true;
        }
        catch
        {
            encoding = null;
            return false;
        }
    }
}
