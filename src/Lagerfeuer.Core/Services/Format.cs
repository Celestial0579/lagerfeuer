using System.Net;
using System.Text.RegularExpressions;

namespace Lagerfeuer.Services;

/// <summary>Anzeigeformate für Zeiten und Beschreibungstexte.</summary>
public static class Format
{
    public static string Time(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} Std. {t.Minutes} Min." : $"{t.Minutes} Min.";
    }

    /// <summary>
    /// Größe von Dateien in der Einheit, die dazu passt: unter 1 GB in MB („28 MB“), darüber in GB mit einer
    /// Nachkommastelle („1,4 GB“). So steht bei wenigen kleinen Büchern nicht „0,0 GB“.
    /// </summary>
    public static string Size(long bytes)
    {
        const double mb = 1024d * 1024, gb = mb * 1024;
        if (bytes <= 0) return "0 MB";
        var megabytes = Math.Round(bytes / mb);
        if (megabytes < 1) return "unter 1 MB";
        // Ab der Zahl, die gerundet 1024 MB ergäbe, in GB weiter.
        return megabytes >= 1024 ? $"{bytes / gb:0.0} GB" : $"{megabytes:0} MB";
    }

    /// <summary>Anzahl mit dem passenden Wort: „1 Buch“, „2 Bücher“.</summary>
    public static string Count(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = Regex.Replace(html, @"<\s*(br|/p|/div|/li)\s*/?>", "\n",
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
    }
}
