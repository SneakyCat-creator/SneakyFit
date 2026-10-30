using System.Windows;
using System.Windows.Controls;
using SneakyFit.Domain;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private void RememberFoodDensity(Food food, decimal? density)
    {
        if (density is null || !FoodUnits.IsValidDensity(density)) return;
        var latest = _session.Data.Foods.FirstOrDefault(f => f.Id == food.Id);
        if (latest is not null && latest.GramsPerMillilitre != density)
            _session.SaveFood(latest with { GramsPerMillilitre = density });
    }

    private void FoodServingsDrawer(Food food, Action<Food> saved)
    {
        var symbol = FoodUnits.Symbol(food.Unit);
        var rows = S();
        var editors = new List<(TextBox Name, TextBox Amount, UIElement Row)>();
        void AddRow(string title = "", decimal? amount = null)
        {
            var name = Input(title); name.MaxLength = 80;
            var quantity = Input(amount is { } value ? N(value, "0.##") : "");
            UIElement? row = null;
            var remove = Round("×", () => { rows.Children.Remove(row!); editors.RemoveAll(e => e.Row == row); }, "#FFFFFF");
            row = M(Between(Pair(Field("Порция", name), Field(symbol, quantity), 2, 1, 10), M(remove, left: 10, top: 18)), bottom: 4);
            editors.Add((name, quantity, row)); rows.Children.Add(row);
        }
        foreach (var serving in food.Servings) AddRow(serving.Name, serving.Amount);
        if (food.Servings.Count == 0) AddRow(food.Unit == FoodUnit.Millilitres ? "Чашка" : "Порция", food.Unit == FoodUnit.Millilitres ? 200 : 250);
        Drawer("Мои порции", S(rows, Btn("+  Порция", () => { if (editors.Count < 100) AddRow(); })), Primary("Сохранить", () => Safe(() =>
        {
            var servings = editors.Select(e => new FoodServing(e.Name.Text.Trim(), Num(e.Amount, "Размер порции", .001m, 100000))).ToList();
            if (!FoodUnits.AreValidServings(servings)) throw new ArgumentException("Назовите порции по-разному и укажите их размер.");
            var latest = _session.Data.Foods.FirstOrDefault(f => f.Id == food.Id) ?? food;
            if (latest.Unit != food.Unit) throw new ArgumentException("Единицы продукта изменились. Откройте порции заново.");
            var updated = latest with { Servings = servings };
            _session.SaveFood(updated); BackDrawer(); saved(updated); Notify("Порции сохранены ✓");
        })));
    }
}
