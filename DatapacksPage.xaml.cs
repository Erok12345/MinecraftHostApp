using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftHostApp.Services;

namespace MinecraftHostApp;

// Датапаки - только для сервера (кладутся прямо в world/datapacks), клиента вообще не касаются.
public partial class DatapacksPage : UserControl
{
    private readonly FirebaseClient _fb;
    private readonly ServerManager _manager;
    private readonly ModrinthClient _modrinth = new();
    private readonly string _serverId;
    private readonly string _mcVersion;
    private readonly bool _isAdmin;

    public bool ChangedSomething { get; private set; }
    public event Action? BackRequested;

    private readonly Dictionary<string, InstalledContentInfo> _installedByProject = new();

    public DatapacksPage(FirebaseClient fb, ServerManager manager, string serverId, string mcVersion, bool isAdmin)
    {
        InitializeComponent();
        _fb = fb; _manager = manager; _serverId = serverId; _mcVersion = mcVersion; _isAdmin = isAdmin;
        TitleText.Text = $"Датапаки: {serverId}";
        Loaded += async (_, _) =>
        {
            await ReloadInstalledAsync();
            await DoSearchAsync();
        };
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private async Task ReloadInstalledAsync()
    {
        var doc = await _fb.GetDocAsync($"servers/{_serverId}");
        InstalledList.Items.Clear();
        _installedByProject.Clear();
        foreach (var entry in doc.GetArray("installedDatapacks"))
        {
            var parts = entry.Split('|');
            var projectId = parts[0];
            var title = parts.Length > 1 ? parts[1] : projectId;
            _installedByProject[projectId] = new InstalledContentInfo { FileName = parts.Length > 2 ? parts[2] : "" };
            InstalledList.Items.Add(BuildInstalledRow(projectId, title));
        }
    }

    // Открыть карточку датапака из списка "УСТАНОВЛЕНО" - тут под рукой нет данных поиска,
    // поэтому сведения подтягиваются с Modrinth напрямую по id.
    private async Task OpenDetailAsync(string projectId)
    {
        StatusMessage.Text = "Загружаю...";
        var mod = await _modrinth.GetProjectSummaryAsync(projectId);
        StatusMessage.Text = "";
        if (mod == null)
        {
            StatusMessage.Text = "Не удалось загрузить сведения о датапаке.";
            return;
        }
        OpenDetail(mod);
    }

    private UIElement BuildInstalledRow(string projectId, string title)
    {
        var border = new Border { Background = (Brush)FindResource("BgCard"), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleText = new TextBlock
        {
            Text = title, Foreground = (Brush)FindResource("AccentGreen"), VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand, TextDecorations = TextDecorations.Underline,
        };
        titleText.MouseLeftButtonUp += async (_, _) => await OpenDetailAsync(projectId);
        grid.Children.Add(titleText);

        if (_isAdmin)
        {
            var removeBtn = new Button { Content = "Удалить", Style = (Style)FindResource("DangerButton"), Width = 100, Height = 30, FontSize = 11.5 };
            removeBtn.Click += async (_, _) =>
            {
                await _manager.RemoveDatapackAsync(_serverId, projectId);
                ChangedSomething = true;
                await ReloadInstalledAsync();
            };
            Grid.SetColumn(removeBtn, 1);
            grid.Children.Add(removeBtn);
        }
        border.Child = grid;
        return border;
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await DoSearchAsync();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await DoSearchAsync();
    }

    private async Task DoSearchAsync()
    {
        var query = SearchBox.Text.Trim();
        ResultsList.Items.Clear();
        StatusMessage.Text = string.IsNullOrEmpty(query) ? "Загружаю популярные датапаки..." : "Ищу на Modrinth...";

        ModSearchResult result;
        try
        {
            result = await _modrinth.SearchContentAsync("datapack", query, _mcVersion);
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"Ошибка поиска: {ex.Message}";
            return;
        }

        StatusMessage.Text = result.Hits.Count == 0 ? "Ничего не найдено." : "";
        foreach (var mod in result.Hits) ResultsList.Items.Add(BuildResultRow(mod));
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

        var textPanel = new StackPanel();
        var titleText = new TextBlock
        {
            Text = mod.Title, FontWeight = FontWeights.SemiBold, FontSize = 15, Foreground = (Brush)FindResource("TextPrimary"),
            Cursor = System.Windows.Input.Cursors.Hand, TextDecorations = TextDecorations.Underline,
        };
        titleText.MouseLeftButtonUp += (_, _) => OpenDetail(mod);
        textPanel.Children.Add(titleText);
        textPanel.Children.Add(new TextBlock { Text = $"автор {mod.Author}", FontSize = 11, Margin = new Thickness(0, 2, 0, 0), Style = (Style)FindResource("SubText") });
        textPanel.Children.Add(new TextBlock
        {
            Text = mod.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            Style = (Style)FindResource("SubText"), MaxHeight = 54, TextTrimming = TextTrimming.CharacterEllipsis
        });

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

        if (_installedByProject.ContainsKey(mod.ProjectId))
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
                Width = 120, Height = 32, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Top,
                Style = (Style)FindResource(_isAdmin ? "PrimaryButton" : "SecondaryButton"),
                Content = _isAdmin ? "Добавить" : "Предложить",
            };
            actionBtn.Click += async (_, _) =>
            {
                actionBtn.IsEnabled = false;
                try
                {
                    if (_isAdmin)
                    {
                        StatusMessage.Text = $"Устанавливаю {mod.Title}...";
                        await _manager.InstallDatapackAsync(_serverId, mod.ProjectId, mod.Title, _mcVersion);
                        StatusMessage.Text = $"{mod.Title} установлен.";
                        ChangedSomething = true;
                        await ReloadInstalledAsync();
                        await DoSearchAsync();
                    }
                    else
                    {
                        var doc = await _fb.GetDocAsync($"servers/{_serverId}");
                        var current = doc.GetArray("pendingDatapacks");
                        if (!current.Any(s => s.StartsWith(mod.ProjectId + "|")))
                        {
                            current = current.Append($"{mod.ProjectId}|{mod.Title}").ToList();
                            await _fb.SetDocAsync($"servers/{_serverId}", new Dictionary<string, object> { ["pendingDatapacks"] = current });
                        }
                        StatusMessage.Text = $"Предложено админу: {mod.Title}.";
                        actionBtn.IsEnabled = true;
                    }
                }
                catch (Exception ex)
                {
                    StatusMessage.Text = $"Ошибка: {ex.Message}";
                    actionBtn.IsEnabled = true;
                }
            };
            Grid.SetColumn(actionBtn, 2);
            grid.Children.Add(actionBtn);
        }

        border.Child = grid;
        return border;
    }

    // Открыть отдельную страницу датапака (описание, что понадобится, все версии) - так же, как у модов.
    private void OpenDetail(ModSummary mod)
    {
        _installedByProject.TryGetValue(mod.ProjectId, out var installed);
        var detail = new ContentDetailControl(_fb, _manager, _modrinth, _serverId, _mcVersion, mod, _isAdmin, ContentKind.Datapack, installed);
        detail.BackRequested += async () =>
        {
            DetailHost.Visibility = Visibility.Collapsed;
            DetailHost.Content = null;
            SearchRoot.Visibility = Visibility.Visible;
            if (detail.ChangedSomething)
            {
                ChangedSomething = true;
                await ReloadInstalledAsync();
                await DoSearchAsync();
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
            panel.Children.Add(new TextBlock { Text = "Обязательно нужно поставить вместе с этим датапаком:", FontSize = 11, Style = (Style)FindResource("SubText") });
            foreach (var dep in required)
                panel.Children.Add(new TextBlock { Text = $"• {dep.Title}", FontSize = 11.5, Foreground = (Brush)FindResource("TextPrimary"), Margin = new Thickness(4, 2, 0, 0) });
        }
        catch
        {
            panel.Children.Clear();
            panel.Children.Add(new TextBlock { Text = "Не удалось загрузить список зависимостей.", FontSize = 11, Style = (Style)FindResource("SubText") });
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
