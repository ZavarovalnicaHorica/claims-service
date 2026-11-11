using System.Globalization;

namespace Horica.ClaimsService.Common;

public static class TextUtil
{
    public static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }

    /// <summary>
    /// Formats an amount the way the web UI does (Slovenian style): 1200.5 -> "1.200,5 €".
    /// Done by hand because the app runs with invariant globalization (no sl-SI culture data).
    /// </summary>
    public static string Euro(decimal amount)
    {
        var invariant = amount.ToString("#,##0.##", CultureInfo.InvariantCulture);
        var slovenian = invariant.Replace(',', '\u0001').Replace('.', ',').Replace('\u0001', '.');
        return slovenian + " €";
    }

    /// <summary>Risk score with two decimals and a dot, e.g. 0.45.</summary>
    public static string Score(double score)
    {
        return score.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
