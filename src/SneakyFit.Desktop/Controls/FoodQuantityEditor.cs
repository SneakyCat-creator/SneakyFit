using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SneakyFit.Domain;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop.Controls;

/// <summary>A portion expressed in mass, volume or a named serving, with an explicit mass/volume relation.</summary>
public sealed class FoodQuantityEditor : StackPanel
{
    private sealed record UnitChoice(string Label, FoodUnit Unit, decimal Factor, bool Serving = false);
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private readonly Food _food;
    private readonly List<UnitChoice> _choices;
    private readonly FrameworkElement _densityField;
    private int _previousIndex;
    private bool _updating;

    public TextBox AmountInput { get; }
    public ComboBox UnitInput { get; }
    public TextBox DensityInput { get; }
    public event Action? Changed;

    public FoodQuantityEditor(Food food, decimal baseAmount, bool metric = false)
    {
        if (!Enum.IsDefined(food.Unit) || !FoodUnits.IsValidDensity(food.GramsPerMillilitre))
            throw new ArgumentException("Проверьте единицы продукта.", nameof(food));
        _food = food;
        _choices =
        [
            new("г", FoodUnit.Grams, 1),
            new("мл", FoodUnit.Millilitres, 1),
            new("л", FoodUnit.Millilitres, 1000),
            new("Порция · 250 г", FoodUnit.Grams, 250, true)
        ];
        _choices.AddRange(food.Servings.Select(s => new UnitChoice(
            $"{s.Name} · {Format(s.Amount)} {FoodUnits.Symbol(food.Unit)}", food.Unit, s.Amount, true)));
        _previousIndex = food.Unit == FoodUnit.Millilitres ? 1 : 0;
        AmountInput = Input(Format(baseAmount), metric);
        AmountInput.TextAlignment = TextAlignment.Left;
        UnitInput = Select(_choices.Select(choice => choice.Label), _previousIndex);
        DensityInput = Input();
        DensityInput.MaxLength = 12;
        AutomationProperties.SetName(AmountInput, "Количество продукта");
        AutomationProperties.SetName(UnitInput, "Единица порции");
        AutomationProperties.SetName(DensityInput, "100 мл весят, г");
        Children.Add(Pair(AmountInput, UnitInput, 1, 1, 10));
        _densityField = M(Field("100 мл весят, г", DensityInput), top: 10);
        Children.Add(_densityField);
        UpdateDensityField();

        AmountInput.TextChanged += (_, _) => { if (!_updating) Changed?.Invoke(); };
        DensityInput.TextChanged += (_, _) => { if (!_updating) Changed?.Invoke(); };
        UnitInput.SelectionChanged += (_, _) => ChangeUnit();
    }

    private UnitChoice Choice => UnitInput.SelectedIndex >= 0 && UnitInput.SelectedIndex < _choices.Count
        ? _choices[UnitInput.SelectedIndex] : throw new ArgumentException("Выберите единицу порции.");

    public FoodUnit SelectedUnit => Choice.Unit;

    public decimal? GramsPerMillilitre
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DensityInput.Text)) return _food.GramsPerMillilitre;
            try { return Number(DensityInput.Text, "Вес 100 мл", 1, 2500) / 100; }
            catch (ArgumentException) when (SelectedUnit == _food.Unit)
            {
                // An unfinished optional conversion must not block returning to the known unit.
                return _food.GramsPerMillilitre;
            }
        }
    }

    public decimal SelectedAmount
    {
        get
        {
            var amount = Number(AmountInput.Text, "Порция", .000001m, 10000) * Choice.Factor;
            if (amount is < .001m or > 10000)
                throw new ArgumentException($"Порция: от 0,001 до 10 000 {FoodUnits.Symbol(SelectedUnit)}.");
            return amount;
        }
    }

    public decimal BaseAmount
    {
        get
        {
            var amount = FoodUnits.ConvertAmount(SelectedAmount, SelectedUnit, _food.Unit, GramsPerMillilitre);
            if (amount is < .001m or > 10000)
                throw new ArgumentException($"Порция: от 0,001 до 10 000 {FoodUnits.Symbol(_food.Unit)}.");
            return amount;
        }
    }

    // This is the serving's snapshot source; the original recipe and catalog entry are not rewritten.
    public Food SelectedFood => _food with
    {
        Unit = SelectedUnit,
        Per100 = FoodUnits.ConvertNutrition(_food.Per100, _food.Unit, SelectedUnit, GramsPerMillilitre),
        GramsPerMillilitre = GramsPerMillilitre
    };

    public void SetBaseAmount(decimal amount)
    {
        if (amount is < .001m or > 10000) throw new ArgumentException("Проверьте размер порции.", nameof(amount));
        _updating = true;
        try
        {
            _previousIndex = _food.Unit == FoodUnit.Millilitres ? 1 : 0;
            UnitInput.SelectedIndex = _previousIndex;
            AmountInput.Text = Format(amount);
            UpdateDensityField();
        }
        finally { _updating = false; }
        Changed?.Invoke();
    }

    private void ChangeUnit()
    {
        if (_updating || UnitInput.SelectedIndex < 0) return;
        var next = Choice;
        var previous = _choices[_previousIndex];
        _updating = true;
        try
        {
            if (next.Serving) AmountInput.Text = "1";
            else
            {
                try
                {
                    var previousAmount = Number(AmountInput.Text, "Порция", .000001m, 10000) * previous.Factor;
                    var converted = FoodUnits.ConvertAmount(previousAmount, previous.Unit, next.Unit, GramsPerMillilitre);
                    AmountInput.Text = Format(converted / next.Factor);
                }
                catch (ArgumentException)
                {
                    // Keep what the user typed while the missing relation is being supplied.
                }
            }
            _previousIndex = UnitInput.SelectedIndex;
            UpdateDensityField();
        }
        finally { _updating = false; }
        Changed?.Invoke();
    }

private void UpdateDensityField() => _densityField.Visibility =
    SelectedUnit != _food.Unit && _food.GramsPerMillilitre is null ? Visibility.Visible : Visibility.Hidden;
    private static string Format(decimal value) => value.ToString("0.########", Russian);

    private static decimal Number(string text, string label, decimal minimum, decimal maximum)
    {
        var normalized = text.Trim().Replace('−', '-').Replace(',', '.').Replace("\u00a0", "").Replace(" ", "");
        if (!decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var result) || result < minimum || result > maximum)
            throw new ArgumentException($"{label}: от {Format(minimum)} до {Format(maximum)}.");
        return result;
    }
}
