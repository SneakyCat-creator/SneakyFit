using System.Text;
using System.Text.RegularExpressions;
using SneakyFit.Domain;

namespace SneakyFit.Backend;

internal static class CatalogSearch
{
    private const string Cyrillic = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
    private static readonly string[] Latin = ["a", "b", "v", "g", "d", "e", "yo", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "kh", "ts", "ch", "sh", "shch", "", "y", "", "e", "yu", "ya"];

    public static string Literal(string query) =>
        string.Join(' ', Tokens(query).Select(t => t is "or" or "and" or "not" ? Quote(t) : t));

    private static string[] Tokens(string query) =>
        Regex.Matches(FoodText.Decode(query).ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => m.Value).ToArray();

    private static string Quote(string token) => "\"" + token + "\"";

    public static IEnumerable<string[]> WordSpellings(string query) => Tokens(query).Distinct()
        .Select(term => new[] { term }.Concat(Spellings(term)).Distinct().ToArray());

    public static IEnumerable<string> Alternatives(string query)
    {
        var terms = Tokens(query);
        if (terms.Length == 1)
        {
            foreach (var spelling in Spellings(terms[0])) yield return spelling;
        }
        else if (terms.Length > 1)
        {
            // OFF can index a brand and a product name in separate fields. A phrase search
            // misses these records. Require every word, allowing each its own spelling.
            var groups = WordSpellings(query).Select(words => "(" + string.Join(" OR ", words.Select(Quote)) + ")");
            yield return string.Join(" AND ", groups);
        }
    }

    private static IEnumerable<string> Spellings(string query)
    {
        var normalized = query.Replace('і', 'и').Replace('ў', 'у');
        if (normalized != query) yield return normalized;
        query = normalized;
        if (!query.Any(c => Cyrillic.Contains(c))) yield break;
        var text = new StringBuilder();
        foreach (var c in query) { var index = Cyrillic.IndexOf(c); text.Append(index < 0 ? c.ToString() : Latin[index]); }
        var standard = text.ToString();
        if (standard.Length < 2) yield break;
        if (standard != query) yield return standard;
        var simplified = standard.Replace("kh", "h").Replace("shch", "sch");
        if (simplified != standard) yield return simplified;
    }
}
