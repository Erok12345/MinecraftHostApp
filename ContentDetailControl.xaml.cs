using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftHostApp.Services;

namespace MinecraftHostApp;

public enum ContentKind { ResourcePack, Datapack }

public class InstalledContentInfo
{
    public string FileName { get; set; } = "";
}

// Общая карточка для ресурспака/датапака (по образцу ModDetailControl) - описание, зависимости
// ("что понадобится" - например шейдерный мод для анимированных ресурспаков) и список всех версий.
public partial class ContentDetailControl : UserControl
{
    private readonly FirebaseClient _fb;
    private readonly ServerManager _manager;
    private readonly ModrinthClient _modrinth;
    private readonly string _serverId;
    private readonly string _mcVersion;
    private readonly ModSummary _mod;
    private readonly bool _isAdmin;
    private readonly ContentKind _kind;
    private InstalledContentInfo? _installed;

    public bool ChangedSomething { get; private set; }
    public event Action? BackRequested;

    private string InstalledField => _kind == ContentKind.ResourcePack ? "installedResourcePacks" : "installedDatapacks";
    private string PendingField => _kind == ContentKind.ResourcePack ? "pendingResourcePacks" : "pendingDatapacks";

    public ContentDetailControl(FirebaseClient fb, ServerManager manager, ModrinthClient modrinth, string serverId,
        string mcVersion, ModSummary mod, bool isAdmin, ContentKind kind, InstalledContentInfo? installed)
    {
        InitializeComponent();
        _fb = fb; _manager = manager; _modrinth = modrinth; _serverId = serverId; _mcVersion = mcVersion;
        _mod = mod; _isAdmin = isAdmin; _kind = kind; _installed = installed;

        TitleText.Text = mod.Title;
        DescriptionText.Text = string.IsNullOrWhiteSpace(mod.Description) ? "Без описания." : mod.Description;
        if (!string.IsNullOrEmpty(mod.IconUrl)) _ = LoadIconAsync(mod.IconUrl);

        KindText.Text = kind == ContentKind.ResourcePack
            ? "Ресурспак - только для клиента, сервера не касается"
            : "Датапак - работает в мире сервера, скачивать друзьям отдельно не нужно";

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

    private async Task RefreshInstalledFromServerAsync()
    {
        var doc = await _fb.GetDocAsync($"servers/{_serverId}");
        var entry = doc.GetArray(InstalledField).FirstOrDefault(s => s.StartsWith(_mod.ProjectId + "|"));
        if (entry == null) { _installed = null; return; }
        var p = entry.Split('|');
        _installed = new InstalledContentInfo { FileName = p.Length > 2 ? p[2] : "" };
    }

    private async Task<string> InstallAsync(ModVersionInfo? specific)
    {
        var file = specific ?? await _modrinth.GetLatestContentVersionAsync(_mod.ProjectId, _mcVersion);
        if (file == null) return $"Для '{_mod.Title}' не нашлось файла под {_mcVersion}.";

        if (_kind == ContentKind.ResourcePack)
            await _manager.InstallResourcePackAsync(_serverId, _mod.ProjectId, _mod.Title, file.FileName, file.FileUrl);
        else
            await _manager.InstallDatapackAsync(_serverId, _mod.ProjectId, _mod.Title, _mcVersion, file);
        return "";
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        ActionButton.IsEnabled = false;
        try
        {
            if (_installed != null)
            {
                if (_kind == ContentKind.ResourcePack) await _manager.RemoveResourcePackAsync(_serverId, _mod.ProjectId);
                else await _manager.RemoveDatapackAsync(_serverId, _mod.ProjectId);
                _installed = null;
                ChangedSomething = true;
                RefreshStatus();
                await LoadVersionsAsync();
            }
            else if (_isAdmin)
            {
                StatusText.Text = "Устанавливаю...";
                var err = await InstallAsync(null);
                await RefreshInstalledFromServerAsync();
                ChangedSomething = true;
                RefreshStatus();
                await LoadVersionsAsync();
                if (!string.IsNullOrEmpty(err)) StatusText.Text = err;
            }
            else
            {
                var doc = await _fb.GetDocAsync($"servers/{_serverId}");
                var current = doc.GetArray(PendingField);
                if (!current.Any(s => s.StartsWith(_mod.ProjectId + "|")))
                {
                    string entry;
                    if (_kind == ContentKind.ResourcePack)
                    {
                        var latest = await _modrinth.GetLatestContentVersionAsync(_mod.ProjectId, _mcVersion);
                        entry = $"{_mod.ProjectId}|{_mod.Title}|{latest?.FileName}|{latest?.FileUrl}";
                    }
                    else
                    {
                        entry = $"{_mod.ProjectId}|{_mod.Title}";
                    }
                    current = current.Append(entry).ToList();
                    await _fb.SetDocAsync($"servers/{_serverId}", new Dictionary<string, object> { [PendingField] = current });
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
            versions = await _modrinth.GetContentVersionsAsync(_mod.ProjectId, _mcVersion);
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
            VersionsList.Items.Add(new TextBlock { Text = $"Нет версий под {_mcVersion}.", FontSize = 12, Style = (Style)FindResource("SubText") });
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
                var err = await InstallAsync(v);
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
