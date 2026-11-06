using System.Net;
using System.Text.RegularExpressions;

namespace SneakyFit.Domain;

public static partial class FoodText
{
    public static string Decode(string? text)
    {
        var value = text ?? "";
        if (!value.Contains('&')) return value;
        for (var pass = 0; pass < 3; pass++)
        {
            var decoded = WebUtility.HtmlDecode(MissingSemicolon().Replace(value, "$0;"));
            if (decoded == value) break;
            value = decoded;
        }
        return value.Replace('\u00a0', ' ');
    }

    [GeneratedRegex(@"&(?:quot|apos|amp|lt|gt|nbsp)(?=\s|$|[«»\""'.,:!?])", RegexOptions.CultureInvariant)]
    private static partial Regex MissingSemicolon();
}
