using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftHostApp.Services;

namespace MinecraftHostApp;

public class InstalledModInfo
{
    public string ClientSide { get; set; } = "unknown";
    public string ServerSide { get; set; } = "unknown";
    public string FileName { get; set; } = "";
}

// Отдельная страница мода (по образцу лобби сервера) - полное описание, что понадобится и
// список всех версий с возможностью поставить любую из них, а не только последнюю.
public partial class ModDetailControl : UserControl
{
    private readonly FirebaseClient _fb;
    private readonly ServerManager _manager;
    private readonly ModrinthClient _modrinth;
    private readonly string _serverId;
    private readonly string _loader;
    private readonly string _mcVersion;
    private readonly ModSummary _mod;
    private readonly bool _isAdmin;
    private InstalledModInfo? _installed;

    public bool ChangedSomething { get; private set; }
    public event Action? BackRequested;

    public ModDetailControl(FirebaseClient fb, ServerManager manager, ModrinthClient modrinth, string serverId,
        string loader, string mcVersion, ModSummary mod, bool isAdmin, InstalledModInfo? installed)
    {
        InitializeComponent();
        _fb = fb; _manager = manager; _modrinth = modrinth; _serverId = serverId; _loader = loader; _mcVersion = mcVersion;
        _mod = mod; _isAdmin = isAdmin; _installed = installed;

        TitleText.Text = mod.Title;
        DescriptionText.Text = string.IsNullOrWhiteSpace(mod.Description) ? "Без описания." : mod.Description;
        if (!string.IsNullOrEmpty(mod.IconUrl)) _ = LoadIconAsync(mod.IconUrl);

        if (mod.IsServerUnsupported)
        {
            CompatText.Text = "⚠ только для клиента - на выделенный сервер не встанет (шейдеры/мини-карта и т.п.)";
            CompatText.Foreground = (Brush)FindResource("AccentRed");
        }
        else if (mod.IsClientUnsupported)
        {
            CompatText.Text = "только для сервера - другу в клиент ставить не нужно";
            CompatText.Foreground = (Brush)FindResource("TextSecondary");
        }
        else
        {
            CompatText.Text = "работает и на сервере, и на клиенте";
            CompatText.Foreground = (Brush)FindResource("TextSecondary");
        }

        RefreshStatus();
        Loaded += async (_, _) =>
        {
            await LoadRequirementsAsync();
            await LoadVersionsAsync();
        };
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void RefreshStatus()
    {
        if (_installed != null)
        {
            StatusText.Text = "✓ Добавлено на сервер";
            StatusText.Foreground = (Brush)FindResource("AccentGreen");
            ActionButton.Content = "Удалить";
            ActionButton.Style = (Style)FindResource("DangerButton");
            ActionButton.Visibility = _isAdmin ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            StatusText.Text = "Не установлен";
            StatusText.Foreground = (Brush)FindResource("TextSecondary");
            ActionButton.Content = _isAdmin ? "Добавить (последняя)" : "Предложить";
            ActionButton.Style = (Style)FindResource(_isAdmin ? "PrimaryButton" : "SecondaryButton");
            ActionButton.Visibility = Visibility.Visible;
        }
    }

    // Подтягивает реальную запись из Firestore после установки/смены версии - чтобы точно знать,
    // какой именно файл сейчас стоит (для пометки "установлена" в списке версий).
    private async Task RefreshInstalledFromServerAsync()
    {
        var doc = await _fb.GetDocAsync($"servers/{_serverId}");
        var entry = doc.GetArray("installedMods").FirstOrDefault(s => s.StartsWith(_mod.ProjectId + "|"));
        if (entry == null) { _installed = null; return; }
        var p = entry.Split('|');
        _installed = new InstalledModInfo
        {
            ClientSide = p.Length > 2 ? p[2] : _mod.ClientSide,
            ServerSide = p.Length > 3 ? p[3] : _mod.ServerSide,
            FileName = p.Length > 4 ? p[4] : "",
        };
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        ActionButton.IsEnabled = false;
        try
        {
            if (_installed != null)
            {
                await _manager.RemoveModAsync(_serverId, _mod.ProjectId);
                _installed = null;
                ChangedSomething = true;
                RefreshStatus();
                await LoadVersionsAsync();
            }
            else if (_isAdmin)
            {
                StatusText.Text = "Устанавливаю...";
                var err = await _manager.InstallModAsync(_serverId, _mod.ProjectId, _mod.Title, _loader, _mcVersion, _mod.ClientSide, _mod.ServerSide);
                await RefreshInstalledFromServerAsync();
                ChangedSomething = true;
                RefreshStatus();
                await LoadVersionsAsync();
                if (!string.IsNullOrEmpty(err)) StatusText.Text = err;
            }
            else
            {
                var doc = await _fb.GetDocAsync($"servers/{_serverId}");
                var current = doc.GetArray("pendingMods");
                if (!current.Any(s => s.StartsWith(_mod.ProjectId + "|")))
                {
                    current = current.Append($"{_mod.ProjectId}|{_mod.Title}|{_fb.Uid}|{_mod.ClientSide}|{_mod.ServerSide}").ToList();
                    await _fb.SetDocAsync($"servers/{_serverId}", new Dictionary<string, object> { ["pendingMods"] = current });
                }
                StatusText.Text = "Предложено админу. Дождись одобрения.";
            }
        }
        finally
        {
            ActionButton.IsEnabled = true;
        }
    }

    private async Task LoadRequirementsAsync()
    {
        RequirementsList.Items.Clear();
        try
        {
            var deps = await _modrinth.GetDependenciesAsync(_mod.ProjectId);
            var required = deps.Where(d => d.DependencyType == "required").ToList();
            if (required.Count == 0)
            {
                RequirementsList.Items.Add(new TextBlock { Text = "Ничего дополнительного не требуется.", FontSize = 12, Style = (Style)FindResource("SubText") });
                return;
            }
            foreach (var dep in required)
                RequirementsList.Items.Add(new TextBlock { Text = $"• {dep.Title}", FontSize = 12.5, Foreground = (Brush)FindResource("TextPrimary"), Margin = new Thickness(0, 2, 0, 0) });
        }
        catch
        {
            RequirementsList.Items.Add(new TextBlock { Text = "Не удалось загрузить список зависимостей.", FontSize = 12, Style = (Style)FindResource("SubText") });
        }
    }

    private async Task LoadVersionsAsync()
    {
        VersionsList.Items.Clear();
        VersionsList.Items.Add(new TextBlock { Text = "Загружаю версии...", FontSize = 12, Style = (Style)FindResource("SubText") });
        List<ModVersionInfo> versions;
        try
        {
            versions = await _modrinth.GetVersionsAsync(_mod.ProjectId, _loader, _mcVersion);
        }
        catch
        {
            VersionsList.Items.Clear();
            VersionsList.Items.Add(new TextBlock { Text = "Не удалось загрузить список версий.", FontSize = 12, Style = (Style)FindResource("SubText") });
            return;
        }

        VersionsList.Items.Clear();
        if (versions.Count == 0)
        {
            VersionsList.Items.Add(new TextBlock { Text = $"Нет версий под {_loader} {_mcVersion}.", FontSize = 12, Style = (Style)FindResource("SubText") });
            return;
        }

        foreach (var v in versions)
            VersionsList.Items.Add(BuildVersionRow(v));
    }

    private UIElement BuildVersionRow(ModVersionInfo v)
    {
        var isActive = _installed != null && _installed.FileName == v.FileName;
        var border = new Border { Background = (Brush)FindResource("BgCard"), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textPanel = new StackPanel();
        textPanel.Children.Add(new TextBlock { Text = v.VersionNumber, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextPrimary") });
        textPanel.Children.Add(new TextBlock { Text = v.Published.ToString("dd.MM.yyyy"), FontSize = 11, Style = (Style)FindResource("SubText") });
        Grid.SetColumn(textPanel, 0);
        grid.Children.Add(textPanel);

        if (isActive)
        {
            var activeText = new TextBlock { Text = "✓ установлена", Foreground = (Brush)FindResource("AccentGreen"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(activeText, 1);
            grid.Children.Add(activeText);
        }
        else if (_isAdmin)
        {
            var btn = new Button
            {
                Content = _installed != null ? "Переключить" : "Поставить",
                Width = 120,
                Height = 30,
                FontSize = 11,
                Style = (Style)FindResource("SecondaryButton"),
            };
            btn.Click += async (_, _) =>
            {
                btn.IsEnabled = false;
                StatusText.Text = "Устанавливаю...";
                var err = await _manager.InstallModAsync(_serverId, _mod.ProjectId, _mod.Title, _loader, _mcVersion, _mod.ClientSide, _mod.ServerSide, v);
                await RefreshInstalledFromServerAsync();
                ChangedSomething = true;
                RefreshStatus();
                await LoadVersionsAsync();
                if (!string.IsNullOrEmpty(err)) StatusText.Text = err;
            };
            Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);
        }

        border.Child = grid;
        return border;
    }

    private async Task LoadIconAsync(string url)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient();
            var bytes = await http.GetByteArrayAsync(url);
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            Icon.Source = bmp;
        }
        catch { /* иконка необязательна */ }
    }
}
