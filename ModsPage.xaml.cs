using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftHostApp.Models;
using MinecraftHostApp.Services;

namespace MinecraftHostApp;

public partial class ModsPage : UserControl
{
    private readonly FirebaseClient _fb;
    private readonly ServerManager _manager;
    private readonly ModrinthClient _modrinth = new();
    private readonly string _serverId;
    private readonly string _loader;
    private readonly string _mcVersion;
    private readonly bool _isAdmin;

    public bool ChangedSomething { get; private set; }
    public event Action? BackRequested;

    private Window? OwnerWindow => Window.GetWindow(this);

    private const int PageSize = 20;
    private int _currentOffset;
    private int _lastTotalHits;
    private bool _filterInitializing;

    private static readonly (string Value, string Label)[] CompatFilterOptions =
    {
        ("all", "Все моды"),
        ("server", "Можно ставить на сервер"),
        ("client", "Только клиентские (шейдеры, мини-карта и т.п.)"),
    };

    private string SelectedCompatFilter => CompatFilterOptions[Math.Max(0, CompatFilterBox.SelectedIndex)].Value;

    private readonly Dictionary<string, InstalledModInfo> _installedByProject = new();

    public ModsPage(FirebaseClient fb, ServerManager manager, string serverId, string loader, string mcVersion, bool isAdmin)
    {
        InitializeComponent();
        _fb = fb; _manager = manager; _serverId = serverId; _loader = loader; _mcVersion = mcVersion; _isAdmin = isAdmin;
        TitleText.Text = $"Моды: {serverId}";

        _filterInitializing = true;
        CompatFilterBox.ItemsSource = CompatFilterOptions.Select(o => o.Label).ToList();
        CompatFilterBox.SelectedIndex = 0;
        _filterInitializing = false;

        Loaded += async (_, _) =>
        {
            await ReloadInstalledAsync();
            await DoSearchAsync(resetPage: true);
        };
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private async Task ReloadInstalledAsync()
    {
        var doc = await _fb.GetDocAsync($"servers/{_serverId}");
        InstalledList.Items.Clear();
        _installedByProject.Clear();
        foreach (var entry in doc.GetArray("installedMods"))
        {
            var parts = entry.Split('|');
            var projectId = parts[0];
            var title = parts.Length > 1 ? parts[1] : projectId;
            var clientSide = parts.Length > 2 ? parts[2] : "unknown";
            var serverSide = parts.Length > 3 ? parts[3] : "unknown";
            var fileName = parts.Length > 4 ? parts[4] : "";
            _installedByProject[projectId] = new InstalledModInfo { ClientSide = clientSide, ServerSide = serverSide, FileName = fileName };
            InstalledList.Items.Add(BuildInstalledRow(projectId, title, clientSide, serverSide));
        }

        PendingList.Items.Clear();
        var pending = doc.GetArray("pendingMods");
        PendingSection.Visibility = (_isAdmin && pending.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        if (_isAdmin)
        {
            foreach (var entry in pending)
            {
                var parts = entry.Split('|');
                if (parts.Length < 2) continue;
                PendingList.Items.Add(BuildPendingRow(parts[0], parts[1], entry));
            }
        }
    }

    private UIElement BuildInstalledRow(string projectId, string title, string clientSide = "unknown", string serverSide = "unknown")
    {
        var border = new Border { Background = (Brush)FindResource("BgCard"), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var textPanel = new StackPanel();
        var titleText = new TextBlock
        {
            Text = title, Foreground = (Brush)FindResource("AccentGreen"), VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand, TextDecorations = TextDecorations.Underline,
        };
        titleText.MouseLeftButtonUp += async (_, _) => await OpenDetailAsync(projectId);
        textPanel.Children.Add(titleText);
        if (serverSide == "unsupported")
            textPanel.Children.Add(new TextBlock { Text = "только клиент - на сервер не установлен", FontSize = 10.5, Foreground = (Brush)FindResource("AccentRed"), Margin = new Thickness(0, 2, 0, 0) });
        else if (clientSide == "unsupported")
            textPanel.Children.Add(new TextBlock { Text = "только сервер - другу в клиент не попадёт", FontSize = 10.5, Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(textPanel, 0);
        grid.Children.Add(textPanel);

        if (_isAdmin)
        {
            var removeBtn = new Button { Content = "Удалить", Style = (Style)FindResource("DangerButton"), Width = 100, Height = 30, FontSize = 11.5 };
            removeBtn.Click += async (_, _) =>
            {
                await _manager.RemoveModAsync(_serverId, projectId);
                ChangedSomething = true;
                await ReloadInstalledAsync();
            };
            Grid.SetColumn(removeBtn, 1);
            grid.Children.Add(removeBtn);
        }
        border.Child = grid;
        return border;
    }

    // Открыть карточку мода из списка "УСТАНОВЛЕНО" - тут под рукой нет данных поиска,
    // поэтому сведения о моде подтягиваются с Modrinth напрямую по id.
    private async Task OpenDetailAsync(string projectId)
    {
        StatusMessage.Text = "Загружаю...";
        var mod = await _modrinth.GetProjectSummaryAsync(projectId);
        StatusMessage.Text = "";
        if (mod == null)
        {
            StatusMessage.Text = "Не удалось загрузить сведения о моде.";
            return;
        }
        OpenDetail(mod);
    }

    private UIElement BuildPendingRow(string projectId, string title, string rawEntry)
    {
        var border = new Border { Background = (Brush)FindResource("BgCard"), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = title, Foreground = (Brush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var entryParts = rawEntry.Split('|');
        var pendingClientSide = entryParts.Length > 3 ? entryParts[3] : "unknown";
        var pendingServerSide = entryParts.Length > 4 ? entryParts[4] : "unknown";

        var approveBtn = new Button { Content = "Одобрить", Style = (Style)FindResource("PrimaryButton"), Width = 100, Height = 30, FontSize = 11.5, Margin = new Thickness(0, 0, 8, 0) };
        approveBtn.Click += async (_, _) =>
        {
            StatusMessage.Text = "Устанавливаю...";
            var err = await _manager.InstallModAsync(_serverId, projectId, title, _loader, _mcVersion, pendingClientSide, pendingServerSide);
            await RemoveFromPendingAsync(rawEntry);
            StatusMessage.Text = string.IsNullOrEmpty(err) ? $"{title} одобрен и установлен." : err;
            ChangedSomething = true;
            await ReloadInstalledAsync();
        };
        Grid.SetColumn(approveBtn, 1);
        grid.Children.Add(approveBtn);

        var rejectBtn = new Button { Content = "Отклонить", Style = (Style)FindResource("SecondaryButton"), Width = 100, Height = 30, FontSize = 11.5 };
        rejectBtn.Click += async (_, _) =>
        {
            await RemoveFromPendingAsync(rawEntry);
            await ReloadInstalledAsync();
        };
        Grid.SetColumn(rejectBtn, 2);
        grid.Children.Add(rejectBtn);

        border.Child = grid;
        return border;
    }

    private async Task RemoveFromPendingAsync(string rawEntry)
    {
        var doc = await _fb.GetDocAsync($"servers/{_serverId}");
        var current = doc.GetArray("pendingMods").Where(s => s != rawEntry).ToList();
        await _fb.SetDocAsync($"servers/{_serverId}", new Dictionary<string, object> { ["pendingMods"] = current });
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await DoSearchAsync(resetPage: true);

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await DoSearchAsync(resetPage: true);
    }

    private async void CompatFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filterInitializing) return;
        await DoSearchAsync(resetPage: true);
    }

    private async void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentOffset <= 0) return;
        _currentOffset = Math.Max(0, _currentOffset - PageSize);
        await DoSearchAsync(resetPage: false);
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        _currentOffset += PageSize;
        await DoSearchAsync(resetPage: false);
    }

    private async Task DoSearchAsync(bool resetPage)
    {
        if (resetPage) _currentOffset = 0;
        var query = SearchBox.Text.Trim();
        ResultsList.Items.Clear();
        StatusMessage.Text = string.IsNullOrEmpty(query) ? "Загружаю популярные моды..." : "Ищу на Modrinth...";

        ModSearchResult result;
        try
        {
            result = await _modrinth.SearchAsync(query, _loader, _mcVersion, _currentOffset, PageSize);
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"Ошибка поиска: {ex.Message}";
            PagerPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _lastTotalHits = result.TotalHits;
        var filter = SelectedCompatFilter;
        var mods = filter switch
        {
            "server" => result.Hits.Where(m => !m.IsServerUnsupported).ToList(),
            "client" => result.Hits.Where(m => m.IsServerUnsupported).ToList(),
            _ => result.Hits,
        };

        StatusMessage.Text = mods.Count == 0 ? "Ничего не найдено на этой странице." : "";
        foreach (var mod in mods)
        {
            ResultsList.Items.Add(BuildResultRow(mod));
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(_lastTotalHits / (double)PageSize));
        var currentPage = _currentOffset / PageSize + 1;
        PageLabel.Text = $"Стр. {currentPage} из {totalPages}";
        PrevPageButton.IsEnabled = _currentOffset > 0;
        NextPageButton.IsEnabled = _currentOffset + PageSize < _lastTotalHits;
        PagerPanel.Visibility = _lastTotalHits > PageSize ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement BuildResultRow(ModSummary mod)
    {
        var border = new Border { Background = (Brush)FindResource("BgCard"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image { Width = 48, Height = 48, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);
        if (!string.IsNullOrEmpty(mod.IconUrl)) _ = LoadIconAsync(icon, mod.IconUrl);

        var isInstalled = _installedByProject.ContainsKey(mod.ProjectId);

        var textPanel = new StackPanel();
        var titleText = new TextBlock
        {
            Text = mod.Title, FontWeight = FontWeights.SemiBold, FontSize = 15, Foreground = (Brush)FindResource("TextPrimary"),
            Cursor = System.Windows.Input.Cursors.Hand, TextDecorations = TextDecorations.Underline,
        };
        titleText.MouseLeftButtonUp += (_, _) => OpenDetail(mod);
        textPanel.Children.Add(titleText);
        textPanel.Children.Add(new TextBlock
        {
            Text = $"автор {mod.Author}", FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
            Style = (Style)FindResource("SubText")
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = mod.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            Style = (Style)FindResource("SubText"), MaxHeight = 54, TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (mod.IsServerUnsupported)
            textPanel.Children.Add(new TextBlock
            {
                Text = "⚠ только для клиента - на выделенный сервер не встанет (шейдеры/мини-карта и т.п.)",
                FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("AccentRed"), Margin = new Thickness(0, 4, 0, 0)
            });
        else if (mod.IsClientUnsupported)
            textPanel.Children.Add(new TextBlock
            {
                Text = "только для сервера - другу в клиент ставить не нужно",
                FontSize = 10.5, Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 4, 0, 0)
            });

        var versionText = new TextBlock { Text = "версия: ...", FontSize = 11, Margin = new Thickness(0, 4, 0, 0), Foreground = (Brush)FindResource("AccentGreen") };
        textPanel.Children.Add(versionText);
        _ = LoadVersionLabelAsync(versionText, mod);

        var requirementsToggle = new TextBlock
        {
            Text = "Что понадобится ▾", FontSize = 11, Cursor = System.Windows.Input.Cursors.Hand,
            Foreground = (Brush)FindResource("AccentGreen"), Margin = new Thickness(0, 6, 0, 0)
        };
        var requirementsPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        var requirementsLoaded = false;
        requirementsToggle.MouseLeftButtonUp += async (_, _) =>
        {
            if (requirementsPanel.Visibility == Visibility.Visible)
            {
                requirementsPanel.Visibility = Visibility.Collapsed;
                requirementsToggle.Text = "Что понадобится ▾";
                return;
            }
            requirementsPanel.Visibility = Visibility.Visible;
            requirementsToggle.Text = "Что понадобится ▴";
            if (requirementsLoaded) return;
            requirementsLoaded = true;
            await LoadRequirementsAsync(requirementsPanel, mod);
        };
        textPanel.Children.Add(requirementsToggle);
        textPanel.Children.Add(requirementsPanel);

        Grid.SetColumn(textPanel, 1);
        grid.Children.Add(textPanel);

        if (isInstalled)
        {
            var addedText = new TextBlock
            {
                Text = "✓ Добавлено", Foreground = (Brush)FindResource("AccentGreen"), FontWeight = FontWeights.SemiBold,
                FontSize = 12, VerticalAlignment = VerticalAlignment.Top, TextAlignment = TextAlignment.Right,
            };
            Grid.SetColumn(addedText, 2);
            grid.Children.Add(addedText);
        }
        else
        {
            var actionBtn = new Button
            {
                Width = 120,
                Height = 32,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Top,
                Style = (Style)FindResource(_isAdmin ? "PrimaryButton" : "SecondaryButton"),
                Content = _isAdmin ? "Добавить" : "Предложить",
            };
            actionBtn.Click += async (_, _) =>
            {
                actionBtn.IsEnabled = false;
                if (_isAdmin)
                {
                    StatusMessage.Text = $"Устанавливаю {mod.Title}...";
                    var err = await _manager.InstallModAsync(_serverId, mod.ProjectId, mod.Title, _loader, _mcVersion, mod.ClientSide, mod.ServerSide);
                    StatusMessage.Text = string.IsNullOrEmpty(err) ? $"{mod.Title} установлен." : err;
                    ChangedSomething = true;
                    await ReloadInstalledAsync();
                    await DoSearchAsync(resetPage: false);
                }
                else
                {
                    var doc = await _fb.GetDocAsync($"servers/{_serverId}");
                    var current = doc.GetArray("pendingMods");
                    if (!current.Any(s => s.StartsWith(mod.ProjectId + "|")))
                    {
                        current = current.Append($"{mod.ProjectId}|{mod.Title}|{_fb.Uid}|{mod.ClientSide}|{mod.ServerSide}").ToList();
                        await _fb.SetDocAsync($"servers/{_serverId}", new Dictionary<string, object> { ["pendingMods"] = current });
                    }
                    StatusMessage.Text = $"Предложено админу: {mod.Title}. Дождись одобрения.";
                    actionBtn.IsEnabled = true;
                }
            };
            Grid.SetColumn(actionBtn, 2);
            grid.Children.Add(actionBtn);
        }

        border.Child = grid;
        return border;
    }

    // Открыть отдельную страницу мода (описание, что понадобится, все версии) - как лобби сервера,
    // только для мода. SearchRoot прячется, вместо него показывается DetailHost.
    private void OpenDetail(ModSummary mod)
    {
        _installedByProject.TryGetValue(mod.ProjectId, out var installed);
        var detail = new ModDetailControl(_fb, _manager, _modrinth, _serverId, _loader, _mcVersion, mod, _isAdmin, installed);
        detail.BackRequested += async () =>
        {
            DetailHost.Visibility = Visibility.Collapsed;
            DetailHost.Content = null;
            SearchRoot.Visibility = Visibility.Visible;
            if (detail.ChangedSomething)
            {
                ChangedSomething = true;
                await ReloadInstalledAsync();
                await DoSearchAsync(resetPage: false);
            }
        };
        DetailHost.Content = detail;
        SearchRoot.Visibility = Visibility.Collapsed;
        DetailHost.Visibility = Visibility.Visible;
    }

    private async Task LoadRequirementsAsync(StackPanel panel, ModSummary mod)
    {
        panel.Children.Add(new TextBlock { Text = "Загружаю...", FontSize = 11, Style = (Style)FindResource("SubText") });
        try
        {
            var deps = await _modrinth.GetDependenciesAsync(mod.ProjectId);
            panel.Children.Clear();
            var required = deps.Where(d => d.DependencyType == "required").ToList();
            if (required.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text = "Ничего дополнительного не требуется.", FontSize = 11, Style = (Style)FindResource("SubText") });
                return;
            }
            panel.Children.Add(new TextBlock { Text = "Обязательно нужно поставить вместе с этим модом:", FontSize = 11, Style = (Style)FindResource("SubText") });
            foreach (var dep in required)
                panel.Children.Add(new TextBlock { Text = $"• {dep.Title}", FontSize = 11.5, Foreground = (Brush)FindResource("TextPrimary"), Margin = new Thickness(4, 2, 0, 0) });
        }
        catch
        {
            panel.Children.Clear();
            panel.Children.Add(new TextBlock { Text = "Не удалось загрузить список зависимостей.", FontSize = 11, Style = (Style)FindResource("SubText") });
        }
    }

    private async Task LoadVersionLabelAsync(TextBlock label, ModSummary mod)
    {
        try
        {
            var info = await _modrinth.GetLatestVersionAsync(mod.ProjectId, _loader, _mcVersion);
            label.Text = info != null ? $"версия: {info.VersionNumber}" : "нет версии под эту сборку";
        }
        catch
        {
            label.Text = "";
        }
    }

    private async Task LoadIconAsync(Image image, string url)
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
            image.Source = bmp;
        }
        catch { /* иконка необязательна */ }
    }
}
