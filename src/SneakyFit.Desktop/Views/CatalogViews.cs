using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SneakyFit.Application;
using SneakyFit.Domain;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private string _catalogQuery = "";
    private DateTimeOffset _catalogRetryAfter;
    private void EnsureCatalogProfile(Guid profileId)
    {
        if (_session.Data.Profile.Id != profileId) throw new OperationCanceledException();
    }
    private Button CatalogAction(string label, Func<Task> action, bool primary = false)
    {
        var button = primary ? Primary(label, () => { }) : Btn(label, () => { });
        button.Click += async (_, _) =>
        {
            if (!button.IsEnabled) return;
            button.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (button.IsLoaded) { if (DrawerOverlay.Visibility == Visibility.Visible) DrawerError.Text = error.Message; else Notify(error.Message); } }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
    private UIElement CatalogSettings()
    {
        var profileId = _session.Data.Profile.Id;
        var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void Draw()
        {
            if (!new CatalogClient(profileId).IsConnected)
            {
                host.Content = Btn("Войти в мой каталог", () => EnrollmentDrawer(Draw));
                return;
            }
            host.Content = S(CatalogAction("Обновить мои продукты", async () =>
            {
                EnsureCatalogProfile(profileId);
                var client = new CatalogClient(profileId); var response = await client.Personal(CancellationToken.None);
                EnsureCatalogProfile(profileId);
                _session.MergeCatalogFoods(response.Products); Notify("Мои продукты обновлены ✓");
            }), M(Btn("Пригласить пользователя", FriendInvitationDrawer), top: 10));
        }
        Draw(); return host;
    }
    private void FriendInvitationDrawer()
    {
        var profileId = _session.Data.Profile.Id; var lifetime = new CancellationTokenSource();
        var body = S();
        Drawer("Пригласить пользователя", body, CatalogAction("Получить код", async () =>
        {
            EnsureCatalogProfile(profileId);
            var invitation = await new CatalogClient(profileId).InviteFriend(lifetime.Token);
            EnsureCatalogProfile(profileId); lifetime.Token.ThrowIfCancellationRequested();
            var code = Input(invitation.Invite); code.IsReadOnly = true;
            code.TextWrapping = TextWrapping.Wrap;
            body.Children.Clear(); body.Children.Add(Field("Одноразовый код · 24 часа", code));
            body.Children.Add(Btn("Скопировать", () => Safe(() => { Clipboard.SetText(invitation.Invite); Notify("Код скопирован"); })));
        }, true));
        _releaseDrawer = lifetime.Cancel;
    }
    private void EnrollmentDrawer(Action? after = null)
    {
        var profileId = _session.Data.Profile.Id;
        var lifetime = new CancellationTokenSource();
        var invite = Input();
        Drawer("Мой каталог", Field("Код приглашения", invite), CatalogAction("Войти", async () =>
        {
            if (string.IsNullOrWhiteSpace(invite.Text)) throw new ArgumentException("Введите код подключения.");
            EnsureCatalogProfile(profileId);
            var client = new CatalogClient(profileId);
            await client.Enroll(invite.Text, lifetime.Token);
            EnsureCatalogProfile(profileId); lifetime.Token.ThrowIfCancellationRequested();
            CloseDrawer(); after?.Invoke(); Notify("Каталог подключён ✓");
        }, true));
        _releaseDrawer = lifetime.Cancel;
    }
    private UIElement OnlineCatalogPanel(Action<CatalogProduct, Food?> selected, string? query = null)
    {
        var profileId = _session.Data.Profile.Id; var lifetime = new CancellationTokenSource();
        var client = new CatalogClient(profileId); var input = Input(query ?? _catalogQuery); input.MaxLength = 120;
        System.Windows.Automation.AutomationProperties.SetName(input, "Поиск продуктов");
        var results = S(); var status = T("", BodySize, Muted); var paging = new ContentControl();
        CancellationTokenSource? pending = null; var currentPage = 1; var requestVersion = 0; var busy = false;
        Button? search = null;
        var cooldown = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void RefreshCooldown()
        {
            var seconds = (int)Math.Ceiling((_catalogRetryAfter - DateTimeOffset.UtcNow).TotalSeconds);
            if (seconds > 0) { search!.IsEnabled = false; search.Content = "Через " + seconds + " с"; cooldown.Start(); }
            else { cooldown.Stop(); search!.IsEnabled = !busy; if (!busy) search.Content = "Повторить"; }
        }
        cooldown.Tick += (_, _) => RefreshCooldown();
        var root = S(); root.Margin = new Thickness(0, 0, 28, 0);
        async Task Search(int page)
        {
            if (busy || DateTimeOffset.UtcNow < _catalogRetryAfter) return;
            var text = input.Text.Trim(); if (text.Length < 2) { status.Text = "Введите хотя бы 2 символа"; return; }
            if (page > 1 && text != _catalogQuery) page = 1;
            if (text.Length > 120) { status.Text = "Слишком длинный запрос"; return; }
            pending?.Cancel(); pending?.Dispose(); pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var cancellation = pending.Token; var version = ++requestVersion;
            busy = true; search!.IsEnabled = false; search.Content = "Ищу…"; results.IsEnabled = false;
            status.Text = "Ищу…"; status.Foreground = B(Muted); paging.Content = null;
            try
            {
                var response = await client.Search(text, page, cancellation, new Progress<string>(message => { if (version == requestVersion && !cancellation.IsCancellationRequested) status.Text = message; }));
                if (version != requestVersion || cancellation.IsCancellationRequested || _session.Data.Profile.Id != profileId) return;
                _catalogQuery = text; currentPage = page; results.Children.Clear();
                foreach (var product in response.Products)
                {
                    Button? item = null;
                    UIElement? resultRow = null;
                    item = CatalogAction("", async () =>
                    {
                        try { await ChooseCatalogProduct(product, selected, lifetime.Token); }
                        catch (CatalogRequestException error) when (error.Status == 404)
                        {
                            if (resultRow is not null) results.Children.Remove(resultRow);
                            status.Text = "Карточка больше недоступна. Выберите другой продукт.";
                        }
                    }); item.Background = B("#FFFFFF"); item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    var detail = string.Join(" · ", new[] { product.Brand, product.Preparation }.Where(v => !string.IsNullOrWhiteSpace(v)));
                    var energy = product.Calories is { } c ? (product.Details?.CaloriesEstimated == true ? "≈ " : "") + N(c) + " ккал" + (product.Basis == "100ml" ? " / 100 мл" : product.Basis == "100g" ? " / 100 г" : "") : "Пищевая ценность частично";
                    item.Content = Between(S(T(product.Name, SubheadingSize, Ink, true), M(T(string.IsNullOrWhiteSpace(detail) ? energy : detail + " · " + energy, CaptionSize, "#65546F"), top: 4),
                        M(T(product.Source == "openfoodfacts" ? "Open Food Facts" : "Общий каталог SneakyFit", CaptionSize, Muted), top: 4)), M(T("+", PageHeadingSize, Purple), left: 16));
                    if (FoodDetails.HasRichInformation(product.Details))
                    {
                        var frame = new Border { Child = item, CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(1.5), Padding = new Thickness(2),
                            BorderBrush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#B79AE7"), (Color)ColorConverter.ConvertFromString("#9BCFC0"), 25) };
                        System.Windows.Automation.AutomationProperties.SetName(item, product.Name + ". Подробный состав и пищевая ценность");
                        resultRow = M(frame, bottom: 9);
                    }
                    else resultRow = M(item, bottom: 9);
                    results.Children.Add(resultRow);
                }
                status.Text = response.Products.Count == 0 ? "Не нашлось" : "";
                var controls = H();
                if (page > 1) controls.Children.Add(CatalogAction("← Назад", () => Search(currentPage - 1)));
                if (response.HasMore) controls.Children.Add(M(CatalogAction("Дальше →", () => Search(currentPage + 1)), left: 9));
                paging.Content = controls.Children.Count > 0 ? controls : null;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (version == requestVersion && !cancellation.IsCancellationRequested)
                {
                    status.Text = error.Message; status.Foreground = B("#A85C65"); search.Content = "Повторить";
                    if (error is CatalogRequestException { RetryAfter: { } delay }) _catalogRetryAfter = DateTimeOffset.UtcNow + delay;
                }
            }
            finally
            {
                if (version == requestVersion)
                {
                    busy = false; results.IsEnabled = true; search.IsEnabled = true;
                    if (search.Content as string == "Ищу…") search.Content = "Найти";
                    if (DateTimeOffset.UtcNow < _catalogRetryAfter) RefreshCooldown();
                }
            }
        }
        search = Primary("Найти", () => { }); search.Click += async (_, _) => await Search(1);
        input.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Search(1); } };
        root.Children.Add(M(Between(input, M(search, left: 12)), bottom: 10));
        var statusRow = M(status, bottom: 10);
        var statusStyle = new Style(typeof(Border));
        var emptyStatus = new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = status }, Value = "" };
        emptyStatus.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed)); statusStyle.Triggers.Add(emptyStatus); statusRow.Style = statusStyle;
        var pagingRow = M(paging, top: 8);
        var pagingStyle = new Style(typeof(Border));
        var emptyPaging = new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(ContentControl.Content)) { Source = paging }, Value = null };
        emptyPaging.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed)); pagingStyle.Triggers.Add(emptyPaging); pagingRow.Style = pagingStyle;
        root.Children.Add(statusRow); root.Children.Add(results); root.Children.Add(pagingRow);
        root.Children.Add(M(Link("", "Open Food Facts · ODbL", () => OpenCatalogSource("https://world.openfoodfacts.org/terms-of-use"), size: CaptionSize), top: 6));
        input.TextChanged += (_, _) =>
        {
            if (!busy) return;
            pending?.Cancel(); requestVersion++; busy = false; results.IsEnabled = true; search.IsEnabled = true; search.Content = "Найти"; status.Text = "";
            if (DateTimeOffset.UtcNow < _catalogRetryAfter) RefreshCooldown();
        };
        root.Loaded += (_, _) => { if (lifetime.IsCancellationRequested) { lifetime.Dispose(); lifetime = new(); } if (DateTimeOffset.UtcNow < _catalogRetryAfter) RefreshCooldown(); };
        root.Unloaded += (_, _) => { lifetime.Cancel(); pending?.Cancel(); requestVersion++; busy = false; cooldown.Stop(); results.IsEnabled = true; search.IsEnabled = true; search.Content = "Найти"; if (status.Text.StartsWith("Ищу") || status.Text.StartsWith("Повторяю")) status.Text = ""; };
        return root;
    }
    private async Task ChooseCatalogProduct(CatalogProduct product, Action<CatalogProduct, Food?> selected, CancellationToken cancellation = default)
    {
        var profileId = _session.Data.Profile.Id;
        var client = new CatalogClient(profileId);
        var local = _session.Data.Foods.FirstOrDefault(food => food.CatalogId == product.Id);
        if (local is not null) { selected(CatalogProduct.FromFood(local), local); return; }
        var personal = client.IsConnected ? (await client.Personal(cancellation)).Products.FirstOrDefault(p => p.Id == product.Id) : null;
        EnsureCatalogProfile(profileId); cancellation.ThrowIfCancellationRequested();
        var resolved = personal ?? await client.Product(product.Id, cancellation);
        EnsureCatalogProfile(profileId); cancellation.ThrowIfCancellationRequested();
        // Canonical revisions are source timestamps, never personal-edit ETags.
        if (personal is null) resolved = resolved with { Revision = null };
        selected(resolved, null);
    }
    private static void OpenCatalogSource(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && (uri.Host == "openfoodfacts.org" || uri.Host.EndsWith(".openfoodfacts.org", StringComparison.OrdinalIgnoreCase)))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
    private void NewSharedFoodDrawer(DateOnly? date = null, Guid? mealId = null, bool planned = false) =>
        ProductDrawer(new CatalogProduct { Source = "community", Basis = "100g" }, date: date, mealId: mealId, planned: planned, create: true);
    private void OnlineBarcodeDrawer(Action<CatalogProduct, Food?>? selected = null)
    {
        var profileId = _session.Data.Profile.Id; var lifetime = new CancellationTokenSource();
        selected ??= (product, food) => ProductDrawer(product, food);
        var client = new CatalogClient(profileId);
        var code = Input(); code.MaxLength = 14; var preview = new ContentControl(); var status = T("", BodySize, Muted);
        async Task Lookup()
        {
            var barcode = code.Text.Trim();
            if (barcode.Length is < 8 or > 14 || barcode.Any(c => c is < '0' or > '9')) throw new ArgumentException("Введите штрихкод из 8–14 цифр.");
            EnsureCatalogProfile(profileId);
            var product = await client.Barcode(barcode, lifetime.Token);
            EnsureCatalogProfile(profileId); lifetime.Token.ThrowIfCancellationRequested();
            await ChooseCatalogProduct(product, selected, lifetime.Token);
        }
        var find = CatalogAction("Найти продукт", Lookup, true);
        code.KeyDown += (_, e) => { if (e.Key == Key.Enter) { find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; } };
        var photo = CatalogAction("Загрузить фото", async () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Фото|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dialog.ShowDialog(this) != true) return;
            if (new FileInfo(dialog.FileName).Length > 25 * 1024 * 1024) throw new ArgumentException("Выберите фото до 25 МБ.");
            var bitmap = LoadBarcodePhoto(dialog.FileName);
            preview.Content = new Image { Source = bitmap, MaxHeight = 250, Stretch = Stretch.Uniform };
            status.Text = "Читаю штрихкод…";
            var found = await Task.Run(() => DecodeBarcode(bitmap));
            EnsureCatalogProfile(profileId); lifetime.Token.ThrowIfCancellationRequested();
            if (found is null) { status.Text = "Штрихкод не виден. Попробуйте фото ближе или введите цифры."; return; }
            code.Text = found; status.Text = "Штрихкод найден"; await Lookup();
        });
        Drawer("По штрихкоду", S(Field("Штрихкод", code), photo, M(preview, top: 16, bottom: 10), status), find);
        _releaseDrawer = lifetime.Cancel;
    }
    private static BitmapSource LoadBarcodePhoto(string path)
    {
        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0]; var width = frame.PixelWidth; var height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || width > 30000 || height > 30000 || (long)width * height > 60000000)
            throw new ArgumentException("Фото слишком большое. Выберите снимок до 60 мегапикселей.");
        input.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) bitmap.DecodePixelWidth = Math.Min(2400, width); else bitmap.DecodePixelHeight = Math.Min(2400, height);
        bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    private static string? DecodeBarcode(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0); var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)]; converted.CopyPixels(pixels, stride, 0);
        var source = new ZXing.RGBLuminanceSource(pixels, converted.PixelWidth, converted.PixelHeight, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32);
        var reader = new ZXing.BarcodeReaderGeneric { AutoRotate = true, Options = new ZXing.Common.DecodingOptions { TryHarder = true, TryInverted = true, PossibleFormats = [ZXing.BarcodeFormat.EAN_13, ZXing.BarcodeFormat.EAN_8, ZXing.BarcodeFormat.UPC_A, ZXing.BarcodeFormat.UPC_E, ZXing.BarcodeFormat.CODE_128] } };
        return reader.Decode(source)?.Text;
    }
}
