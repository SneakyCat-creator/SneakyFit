using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SneakyFit.Application;
using SneakyFit.Domain;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private static string IndexText(GlycemicRange? value, string format = "0.#") => value is null ? "—" :
        value.Low == value.High ? N(value.Low, format) : N(value.Low, format) + "–" + N(value.High, format);

    private UIElement GlycemicInformation(CatalogProduct product, Food food, decimal amount)
    {
        var result = Glycemic.Resolve(product.Name, product.Preparation, product.Details, product.Carbs, product.Fat, food.Recipe);
        var load = Glycemic.Load(result, product.Carbs, amount);
        var estimated = result?.Kind is "estimated" or "category-estimate" or "personal-estimate" or "recipe";
        var prefix = estimated ? "≈ " : "";
        var root = S(Pair(
            S(T("Гликемический индекс · ГИ", CaptionSize, "#655F72"), M(T(prefix + IndexText(result?.Index), MetricSize, Purple, true), top: 5)),
            S(T("Гликемическая нагрузка · ГН", CaptionSize, "#655F72"), M(T((load is not null ? prefix : "") + IndexText(load), MetricSize, Purple, true), top: 5)), gap: 12));
        var reason = product.Carbs == 0 ? "Нет усвояемых углеводов: ГИ не применяется, расчётная ГН — 0."
            : product.Carbs is > 0 and < 5 ? "Мало углеводов: ГИ обычно не измеряют. Без подходящего аналога ГН не рассчитываем."
            : "Пока нет подходящего аналога. Одних КБЖУ для расчёта ГИ недостаточно.";
        root.Children.Add(M(T(result?.Description ?? reason, CaptionSize, "#655F72"), top: 10));
        if (load is not null) root.Children.Add(M(T("ГН на " + N(amount, "0.##") + " " + FoodUnits.Symbol(food.Unit), CaptionSize, "#655F72"), top: 4));
        if (result is { Kind: "measured-analogue", Studies.Count: > 0 })
        {
            var study = result.Studies[0];
            root.Children.Add(M(T(study.Food + " · " + study.Preparation, CaptionSize), top: 9));
        }
        if (result is not null && (result.Studies.Count > 0 || result.References.Count > 0))
        {
            var sources = S();
            foreach (var study in result.Studies.DistinctBy(s => s.Url))
                sources.Children.Add(M(Link("", study.Citation, () => OpenNutritionReference(study.Url), size: CaptionSize), top: 6));
            foreach (var source in result.References.DistinctBy(s => s.Url))
                sources.Children.Add(M(Link("", source.Title, () => OpenNutritionReference(source.Url), size: CaptionSize), top: 6));
            if (result.Kind == "category-estimate") sources.Children.Add(M(T("Округлённый ориентир SneakyFit по похожей еде. Это не измерение этой упаковки и не гарантированные границы ГИ.", CaptionSize, "#655F72"), top: 8));
            root.Children.Add(new Expander { Header = "Источники", Content = sources, FontSize = CaptionSize, Margin = new Thickness(0, 8, 0, 0) });
        }
        return Surface(root, "#F1ECFA", 16, 18);
    }

    private Button NutritionHelp(CatalogProduct product, Food food, decimal amount)
    {
        var button = Round("", () => { }, "#F1ECFA", Ink); button.Width = button.Height = 30;
        button.Content = Icon("\uE946", 17, Ink);
        AutomationProperties.SetName(button, "Пояснения к пищевой ценности");
        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Custom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            CustomPopupPlacementCallback = (size, target, _) => [new(new Point(target.Width - size.Width, target.Height + 6), PopupPrimaryAxis.Vertical)] };
        var rows = S();
        var close = Round("×", () => popup.IsOpen = false, "#F1ECFA", Ink); close.Width = close.Height = 28;
        var header = Between(T("Что означают показатели", SubheadingSize, Ink, true), close);
        void Entry(string name, string explanation) => rows.Children.Add(M(S(T(name, BodySize, Ink, true), M(T(explanation, BodySize), top: 3)), top: 12));
        Entry("КБЖУ", "Калории, белки, жиры и углеводы. В таблице — на 100 г / мл и на выбранную порцию.");
        Entry("Сахара и жиры", "Сахара уже входят в углеводы, а насыщенные и другие виды жиров — в общие жиры. Складывать их повторно не нужно. Соль и натрий связаны: 1 г натрия соответствует примерно 2,5 г соли.");
        Entry("ГИ · гликемический индекс", "Насколько углеводы продукта повышают глюкозу крови относительно глюкозы с индексом 100. Это характеристика продукта, а не размер порции.");
        Entry("ГН · гликемическая нагрузка", "Учитывает порцию: ГИ × усвояемые углеводы в порции ÷ 100. Например: 60 × 30 г ÷ 100 = 18.");
        var gi = Glycemic.Resolve(product.Name, product.Preparation, product.Details, product.Carbs, product.Fat, food.Recipe);
        if (gi is not null && Glycemic.Load(gi, product.Carbs, amount) is { } gl)
            rows.Children.Add(M(T("Ваша порция: " + IndexText(gi!.Index) + " × " + N(product.Carbs!.Value * amount / 100, "0.##") + " г ÷ 100 = " + IndexText(gl), BodySize, Purple, true), top: 8));
        Entry("Измерение и оценка", "Измеренный ГИ относится к конкретному продукту и приготовлению в исследовании. Для похожей еды показываем оценку диапазоном. ГН наследует эту неопределённость; это не прогноз сахара крови человека.");
        Entry("Nutri-Score", "Оценка пищевого состава по шкале A–E: от более благоприятного к менее благоприятному. Удобна для сравнения похожих продуктов.");
        Entry("NOVA", "Степень обработки: 1 — минимальная; 2 — кулинарные ингредиенты; 3 — обработанные; 4 — ультраобработанные продукты.");
        Entry("Единицы и знаки", "ккал — килокалории; г — граммы; мг — миллиграммы; мкг — микрограммы; мл — миллилитры. 1 г = 1000 мг = 1 000 000 мкг. «≈» — расчёт или оценка; «<» — меньше; «>» — больше; «—» — данных нет, это не ноль.");
        Entry("Состав", "B1–B12, A, C, D, E, K — витамины. E с номером — код пищевой добавки. «Следы» означают возможное присутствие аллергена; отсутствие списка не гарантирует его отсутствие в продукте.");
        rows.Margin = new Thickness(0, 0, 14, 0);
        var scroll = new ScrollViewer { Content = rows, MaxHeight = Math.Max(200, Math.Min(400, ActualHeight - 200)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var surface = Surface(S(header, scroll), "#FFFCFF", 18, 20); surface.Width = 410; surface.MaxWidth = Math.Max(300, ActualWidth - 80);
        surface.BorderThickness = new Thickness(1); surface.BorderBrush = B("#E1D6F1"); popup.Child = surface;
        button.Click += (_, _) => popup.IsOpen = !popup.IsOpen;
        popup.Opened += (_, _) => scroll.ScrollToTop();
        button.Unloaded += (_, _) => popup.IsOpen = false;
        return button;
    }

    private static void OpenNutritionReference(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host is not ("doi.org" or "glycemicindex.com" or "pubmed.ncbi.nlm.nih.gov")) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
