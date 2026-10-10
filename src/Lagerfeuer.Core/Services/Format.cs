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
