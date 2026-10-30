using System.Globalization;
using SneakyFit.Domain;

namespace SneakyFit.Application;

public enum ProgressShareKind { Weight, Measurements, Nutrition, Activity }

public sealed record ProgressShareMessage(string Text)
{
    public const string DownloadUrl = "https://github.com/SneakyCat-creator/SneakyFit/releases/latest/download/SneakyFit.exe";
    public string FullText => Text + "\n" + DownloadUrl;
    public Uri Telegram => new("https://t.me/share/url?url=" + Uri.EscapeDataString(DownloadUrl) + "&text=" + Uri.EscapeDataString(Text));
    public Uri WhatsApp => new("https://wa.me/?text=" + Uri.EscapeDataString(FullText));
}

public static class ProgressShare
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static ProgressShareMessage Create(Workspace workspace, ProgressShareKind kind, DateOnly from, DateOnly to,
        string measurementKey = "waist", BodySide side = BodySide.None)
    {
        if (from > to) throw new ArgumentException("Проверьте период.");
        bool InPeriod(DateTime time) => DateOnly.FromDateTime(time) >= from && DateOnly.FromDateTime(time) <= to;
        var details = new List<string>();
        if (kind == ProgressShareKind.Weight)
        {
            var entries = workspace.Weight.Where(w => InPeriod(w.RecordedAt)).OrderBy(w => w.RecordedAt).ToArray();
            if (entries.Length == 0) details.Add("Пока нет записей о весе за этот период.");
            else details.AddRange(Change("Вес", entries.Select(w => (w.RecordedAt, w.Kilograms)).ToArray(), "кг"));
        }
        else if (kind == ProgressShareKind.Measurements)
        {
            var definition = MeasurementCatalog.All.FirstOrDefault(m => m.Key == measurementKey)
                ?? throw new ArgumentException("Выберите параметр.");
            var chosenSide = definition.Bilateral ? side == BodySide.Right ? BodySide.Right : BodySide.Left : BodySide.None;
            var label = definition.Name + (definition.Bilateral ? chosenSide == BodySide.Left ? " · слева" : " · справа" : "");
            var entries = workspace.Measurements.Where(m => m.Key == measurementKey && m.Side == chosenSide && InPeriod(m.RecordedAt))
                .OrderBy(m => m.RecordedAt).ToArray();
            if (entries.Length == 0) details.Add(label + ": пока нет записей за этот период.");
            else details.AddRange(Change(label, entries.Select(m => (m.RecordedAt, m.Value)).ToArray(), "см"));
        }
        else
        {
            var days = workspace.Days.Where(d => d.Date >= from && d.Date <= to && d.Status != DayStatus.Planned).OrderBy(d => d.Date).ToArray();
            if (kind == ProgressShareKind.Nutrition)
            {
                var recorded = days.Where(d => d.HasFoodRecord).ToArray();
                if (recorded.Length == 0) details.Add("Пока нет записей о еде за этот период.");
                else
                {
                    var totals = recorded.Select(d => d.Total(EntryKind.Eaten)).ToArray();
                    details.Add("Дней с записью еды: " + recorded.Length + ".");
                    details.Add("В среднем за эти дни: " + Number(totals.Average(n => n.Calories), "0") + " ккал · Б " + Number(totals.Average(n => n.Protein)) +
                        " г · Ж " + Number(totals.Average(n => n.Fat)) + " г · У " + Number(totals.Average(n => n.Carbs)) + " г.");
                }
            }
            else if (kind == ProgressShareKind.Activity)
            {
                var recorded = days.Where(d => d.Activities.Count > 0).ToArray();
                if (recorded.Length == 0) details.Add("Пока нет записей об активности за этот период.");
                else
                {
                    var stepDays = recorded.Where(d => d.Activities.Any(a => a.Kind == ActivityKind.Steps)).ToArray();
                    if (stepDays.Length > 0)
                    {
                        var steps = stepDays.Sum(d => d.Activities.Where(a => a.Kind == ActivityKind.Steps).Sum(a => (long)a.Steps));
                        details.Add("Шаги: " + Number(steps, "N0") + " за " + Days(stepDays.Length) + " с записями.");
                        details.Add("В среднем за эти дни: " + Number((decimal)steps / stepDays.Length, "N0") + " шагов.");
                    }
                    var workouts = recorded.SelectMany(d => d.Activities).Where(a => a.Kind == ActivityKind.Workout).ToArray();
                    if (workouts.Length > 0) details.Add("Тренировки: " + workouts.Length + " · " + Number(workouts.Sum(a => a.Minutes), "0.#") + " мин.");
                    var indexes = recorded.Select(d => ActivityIndex.ForDay(workspace, d)).OfType<DailyActivityIndex>().ToArray();
                    if (indexes.Length > 0) details.Add("Средний индекс активности: " + Number((decimal)indexes.Average(a => a.Percent), "0") + "%.");
                }
            }
            else throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var period = from == DateOnly.MinValue ? "за всё время" : "за " + Range(from, to);
        return new("Мой прогресс в SneakyFit " + period + ".\n" + string.Join("\n", details) + "\n\nПопробуй SneakyFit тоже:");
    }

    private static IEnumerable<string> Change(string label, (DateTime At, decimal Value)[] entries, string unit)
    {
        var first = entries[0]; var last = entries[^1];
        var lastValue = Number(last.Value, "0.##") + " " + unit + " (" + last.At.ToString("dd.MM.yyyy", Ru) + ")";
        if (entries.Length == 1) return [label + ": " + lastValue + "."];
        var difference = last.Value - first.Value;
        var change = difference == 0 ? "Без изменений." : "Изменение: " + (difference < 0 ? "−" : "+") + Number(Math.Abs(difference), "0.##") + " " + unit + ".";
        return [label + ": " + Number(first.Value, "0.##") + " " + unit + " (" + first.At.ToString("dd.MM.yyyy", Ru) + ") → " + lastValue + ".", change];
    }

    private static string Range(DateOnly from, DateOnly to) => from == to ? to.ToString("d MMMM yyyy", Ru)
        : from.ToString(from.Year == to.Year ? "d MMMM" : "d MMMM yyyy", Ru) + " — " + to.ToString("d MMMM yyyy", Ru);
    private static string Number(decimal value, string format = "0.#") => value.ToString(format, Ru);
    private static string Days(int count) => count + " " + (count % 100 is >= 11 and <= 14 ? "дней" : (count % 10) switch { 1 => "день", >= 2 and <= 4 => "дня", _ => "дней" });
}
