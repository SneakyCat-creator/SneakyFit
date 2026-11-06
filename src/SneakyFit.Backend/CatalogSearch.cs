using System.Text;

namespace SneakyFit.Backend;

internal static class CatalogSearch
{
    private const string Cyrillic = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
    private static readonly string[] Latin = ["a", "b", "v", "g", "d", "e", "yo", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "kh", "ts", "ch", "sh", "shch", "", "y", "", "e", "yu", "ya"];

    public static IEnumerable<string> Spellings(string query)
    {
        query = query.ToLowerInvariant();
        if (!query.Any(c => Cyrillic.Contains(c))) yield break;
        var text = new StringBuilder();
        foreach (var c in query) { var index = Cyrillic.IndexOf(c); text.Append(index < 0 ? c.ToString() : Latin[index]); }
        var standard = text.ToString();
        if (standard.Length < 2) yield break;
        yield return standard;
        var simplified = standard.Replace("kh", "h").Replace("shch", "sch");
        if (simplified != standard) yield return simplified;
    }
}
