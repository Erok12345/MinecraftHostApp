using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftHostApp.Models;
using MinecraftHostApp.Services;

namespace MinecraftHostApp;

public class ServerCardViewModel
{
    public string Id { get; set; } = "";
    public string Loader { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string LoaderVersion { get; set; } = "";
    public string StatusText { get; set; } = "";
    public Brush StatusColor { get; set; } = Brushes.Gray;
    public bool IsMine { get; set; }
}

public class FriendViewModel
{
    public string Uid { get; set; } = "";
    public string Name { get; set; } = "";
    public Brush AvatarBrush { get; set; } = Brushes.Transparent;
}

public partial class MainWindow : Window
{
    private AppConfig _config = new();
    private FirebaseClient _fb = null!;
    private ServerManager _manager = null!;
    private bool _signUpMode = false;
    private List<ServerCardViewModel> _allServers = new();
    private bool _showMineOnly = false;

    private static readonly Brush FreeColor = new SolidColorBrush(Color.FromRgb(0x5F, 0xBF, 0x6B));
    private static readonly Brush BusyColor = new SolidColorBrush(Color.FromRgb(0xE5, 0x60, 0x60));

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _config = AppConfig.Load();
        _fb = new FirebaseClient(_config);
        _manager = new ServerManager(_config, _fb);

        LoginEmailBox.Text = _config.Email;
    }

    private void ToggleMode_Click(object sender, RoutedEventArgs e)
    {
        _signUpMode = !_signUpMode;
        if (_signUpMode)
        {
            AuthTitleText.Text = "Регистрация";
            AuthSubText.Text = "Создайте аккаунт, чтобы видеть серверы";
            NicknameLabel.Visibility = Visibility.Visible;
            NicknameBox.Visibility = Visibility.Visible;
            LoginButton.Content = "Зарегистрироваться";
            ToggleModeButton.Content = "Уже есть аккаунт? Войти";
        }
        else
        {
            AuthTitleText.Text = "Minecraft Rotating Host";
            AuthSubText.Text = "Войдите, чтобы увидеть серверы";
            NicknameLabel.Visibility = Visibility.Collapsed;
            NicknameBox.Visibility = Visibility.Collapsed;
            LoginButton.Content = "Войти";
            ToggleModeButton.Content = "Нет аккаунта? Зарегистрироваться";
        }
        LoginStatusText.Text = "";
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        _config.Email = LoginEmailBox.Text.Trim();
        _config.Password = LoginPasswordBox.Password;

        if (string.IsNullOrWhiteSpace(_config.Email) || string.IsNullOrWhiteSpace(_config.Password))
        {
            LoginStatusText.Text = "Заполни email и пароль.";
            return;
        }

        LoginButton.IsEnabled = false;
        LoginStatusText.Text = _signUpMode ? "Регистрирую..." : "Вхожу...";

        var (ok, error) = _signUpMode ? await _fb.SignUpAsync() : await _fb.SignInAsync();
        LoginButton.IsEnabled = true;

        if (!ok)
        {
            LoginStatusText.Text = error;
            return;
        }

        if (_signUpMode)
        {
            var nickname = string.IsNullOrWhiteSpace(NicknameBox.Text) ? _config.Email.Split('@')[0] : NicknameBox.Text.Trim();
            await _fb.SetDisplayNameAsync(nickname);
            await _fb.SendEmailVerificationAsync();
            MessageWindow.Show(this,
                "Аккаунт создан! На твою почту отправлено письмо для подтверждения email - " +
                "проверь папку 'Входящие' (и 'Спам').\nМожно продолжать пользоваться приложением уже сейчас.",
                "Готово");
        }

        _config.Save();
        await EnterMainScreenAsync();
    }

    private async Task EnterMainScreenAsync()
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;
        UidText.Text = $"UID: {_fb.Uid}";

        var name = await _fb.GetDisplayNameAsync();
        var finalName = string.IsNullOrWhiteSpace(name) ? _config.Email.Split('@')[0] : name;
        ProfileNameText.Text = finalName;
        // Публикуем ник в общий каталог users/{uid} - на случай если аккаунт создан до появления этого каталога.
        await _fb.SetDocAsync($"users/{_fb.Uid}", new Dictionary<string, object> { ["displayName"] = finalName });

        _avatarUrl = await _fb.GetAvatarUrlAsync(_fb.Uid!);
        if (!string.IsNullOrWhiteSpace(_avatarUrl)) SetAvatarImage(_avatarUrl);

        await RefreshFriendsAsync();
        await RefreshServersAsync();
    }

    private string _avatarUrl = "";

    private void SetAvatarImage(string url)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            AvatarEllipse.Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
        }
        catch { /* аватар необязателен */ }
    }

    // Клик по аватарке/ссылке "Профиль" открывает страницу профиля - там уже сама смена фото,
    // не сразу диалог выбора файла (чтобы случайный клик по аватарке не открывал загрузку).
    private void OpenProfile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var profile = new ProfileControl(_fb, _config, _avatarUrl, ProfileNameText.Text);
        profile.AvatarChanged += url => { _avatarUrl = url; SetAvatarImage(url); };
        profile.NicknameChanged += nick => ProfileNameText.Text = nick;
        profile.BackRequested += () =>
        {
            LobbyHost.Visibility = Visibility.Collapsed;
            LobbyHost.Content = null;
            ServerListPanel.Visibility = Visibility.Visible;
        };
        LobbyHost.Content = profile;
        ServerListPanel.Visibility = Visibility.Collapsed;
        LobbyHost.Visibility = Visibility.Visible;
    }

    private void CopyUid_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!string.IsNullOrEmpty(_fb.Uid)) Clipboard.SetText(_fb.Uid);
    }

    private async void AddFriend_Click(object sender, RoutedEventArgs e)
    {
        var uid = AddFriendBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(uid)) return;
        if (uid == _fb.Uid)
        {
            MessageWindow.Show(this, "Это твой собственный UID.", "Ошибка");
            return;
        }
        await _fb.AddFriendAsync(uid);
        AddFriendBox.Text = "";
        await RefreshFriendsAsync();
    }

    private async void RemoveFriend_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        var uid = (string)((FrameworkElement)sender).Tag;
        await _fb.RemoveFriendAsync(uid);
        await RefreshFriendsAsync();
    }

    private async Task RefreshFriendsAsync()
    {
        var uids = await _fb.GetFriendsAsync();
        var list = new List<FriendViewModel>();
        foreach (var uid in uids) list.Add(new FriendViewModel { Uid = uid });
        FriendsList.ItemsSource = list;

        // Имя и аватарку подгружаем параллельно после того, как список уже показан -
        // чтобы карточки друзей не ждали все сетевые запросы разом.
        await Task.WhenAll(list.Select(async vm =>
        {
            vm.Name = await _fb.GetPublicDisplayNameAsync(vm.Uid);
            var avatarUrl = await _fb.GetAvatarUrlAsync(vm.Uid);
            if (!string.IsNullOrWhiteSpace(avatarUrl)) vm.AvatarBrush = await LoadAvatarBrushAsync(avatarUrl);
        }));
        FriendsList.ItemsSource = null;
        FriendsList.ItemsSource = list;
    }

    private static async Task<Brush> LoadAvatarBrushAsync(string url)
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
            var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    private async void FriendCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var uid = (string)((FrameworkElement)sender).Tag;
        var canView = await _fb.CanViewServersAsync(uid);
        var name = await _fb.GetPublicDisplayNameAsync(uid);

        var profile = new FriendProfileControl(_fb, name, uid, canView);
        profile.BackRequested += () =>
        {
            LobbyHost.Visibility = Visibility.Collapsed;
            LobbyHost.Content = null;
            ServerListPanel.Visibility = Visibility.Visible;
        };
        LobbyHost.Content = profile;
        ServerListPanel.Visibility = Visibility.Collapsed;
        LobbyHost.Visibility = Visibility.Visible;
    }

    private async void EditNickname_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var nick = InputWindow.Ask(this, "Ник", "Новый никнейм:", defaultValue: ProfileNameText.Text);
        if (string.IsNullOrWhiteSpace(nick)) return;
        await _fb.SetDisplayNameAsync(nick.Trim());
        ProfileNameText.Text = nick.Trim();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshServersAsync();

    private async Task RefreshServersAsync()
    {
        var servers = await _fb.ListCollectionAsync("servers");
        var cards = new List<ServerCardViewModel>();
        foreach (var s in servers)
        {
            if (s.Doc.GetString("visible") == "false") continue;
            var session = await _fb.GetDocAsync($"sessions/{s.Id}");
            bool hosting = session.Exists && session.GetString("status") == "hosting";
            bool isMine = s.Doc.GetString("createdBy") == _fb.Uid
                          || s.Doc.GetArray("allowedHosts").Contains(_fb.Uid!)
                          || _config.KnownServerPasswords.ContainsKey(s.Id);
            cards.Add(new ServerCardViewModel
            {
                Id = s.Id,
                Loader = s.Doc.GetString("loader"),
                McVersion = s.Doc.GetString("mcVersion"),
                LoaderVersion = $"{s.Doc.GetString("loader")} {s.Doc.GetString("mcVersion")}",
                StatusText = hosting ? "Хостит кто-то" : "Свободен",
                StatusColor = hosting ? BusyColor : FreeColor,
                IsMine = isMine,
            });
        }
        _allServers = cards;

        var loaders = new List<string> { "Все загрузчики" }.Concat(_allServers.Select(c => c.Loader).Distinct().OrderBy(x => x)).ToList();
        var versions = new List<string> { "Все версии" }.Concat(_allServers.Select(c => c.McVersion).Distinct().OrderByDescending(x => x)).ToList();
        var prevLoader = LoaderFilterBox.SelectedItem as string;
        var prevVersion = VersionFilterBox.SelectedItem as string;
        LoaderFilterBox.ItemsSource = loaders;
        VersionFilterBox.ItemsSource = versions;
        LoaderFilterBox.SelectedIndex = prevLoader != null && loaders.Contains(prevLoader) ? loaders.IndexOf(prevLoader) : 0;
        VersionFilterBox.SelectedIndex = prevVersion != null && versions.Contains(prevVersion) ? versions.IndexOf(prevVersion) : 0;

        ApplySearchFilter();
    }

    private void ApplySearchFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        IEnumerable<ServerCardViewModel> result = _allServers;

        if (_showMineOnly) result = result.Where(s => s.IsMine);
        if (LoaderFilterBox.SelectedItem is string loader && loader != "Все загрузчики")
            result = result.Where(s => s.Loader == loader);
        if (VersionFilterBox.SelectedItem is string version && version != "Все версии")
            result = result.Where(s => s.McVersion == version);
        if (!string.IsNullOrEmpty(query))
            result = result.Where(s => s.Id.Contains(query, StringComparison.OrdinalIgnoreCase));

        ServerList.ItemsSource = result.ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ApplySearchFilter();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplySearchFilter();

    private void TabAll_Click(object sender, RoutedEventArgs e)
    {
        _showMineOnly = false;
        TabAllButton.Style = (Style)FindResource("PrimaryButton");
        TabMineButton.Style = (Style)FindResource("SecondaryButton");
        ApplySearchFilter();
    }

    private void TabMine_Click(object sender, RoutedEventArgs e)
    {
        _showMineOnly = true;
        TabMineButton.Style = (Style)FindResource("PrimaryButton");
        TabAllButton.Style = (Style)FindResource("SecondaryButton");
        ApplySearchFilter();
    }

    private async void ServerCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var id = (string)((FrameworkElement)sender).Tag;
        await OpenServerAsync(id);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Canvas.SetLeft(BlobTopRight, ActualWidth - 380);
        Canvas.SetTop(BlobTopRight, -120);
        Canvas.SetLeft(BlobBottom, ActualWidth / 2 - 180);
        Canvas.SetTop(BlobBottom, ActualHeight - 260);
        Canvas.SetLeft(BlobMid, ActualWidth / 2 - 150);
        Canvas.SetTop(BlobMid, ActualHeight / 2 - 150);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var id = (string)((FrameworkElement)sender).Tag;
        await OpenServerAsync(id);
    }

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        var id = InputWindow.Ask(this, "Найти сервер", "Точное название сервера:");
        if (!string.IsNullOrWhiteSpace(id)) await OpenServerAsync(id.Trim());
    }

    private bool EnsureHostSetup()
    {
        if (_config.IsHostReady()) return true;
        MessageWindow.Show(this,
            "Чтобы стать хостом, сначала нужно один раз указать папку Google Drive и адрес playit на этом ПК. " +
            "Открываю Настройки - заполни и нажми 'Создать сервер' ещё раз.",
            "Нужна настройка");
        ShowSettings();
        return false;
    }

    private void ShowSettings()
    {
        var settings = new SettingsControl(_config, _fb);
        settings.BackRequested += () =>
        {
            LobbyHost.Visibility = Visibility.Collapsed;
            LobbyHost.Content = null;
            ServerListPanel.Visibility = Visibility.Visible;
        };
        LobbyHost.Content = settings;
        ServerListPanel.Visibility = Visibility.Collapsed;
        LobbyHost.Visibility = Visibility.Visible;
    }

    private static bool IsKickedNow(FirestoreDoc server, string uid)
    {
        var entry = server.GetArray("kickedUntil").FirstOrDefault(s => s.StartsWith(uid + "|"));
        if (entry == null) return false;
        var parts = entry.Split('|', 2);
        return parts.Length == 2 && DateTime.TryParse(parts[1], out var until) && until > DateTime.UtcNow;
    }

    private async Task OpenServerAsync(string serverId)
    {
        var server = await _fb.GetDocAsync($"servers/{serverId}");
        if (!server.Exists)
        {
            MessageWindow.Show(this, $"Сервер '{serverId}' не найден.", "Ошибка");
            return;
        }

        if (server.GetArray("bannedUids").Contains(_fb.Uid!))
        {
            MessageWindow.Show(this, "Тебя забанили на этом сервере.", "Доступ закрыт");
            return;
        }
        if (IsKickedNow(server, _fb.Uid!))
        {
            MessageWindow.Show(this, "Тебя временно кикнули с этого сервера - попробуй позже.", "Доступ закрыт");
            return;
        }

        string? pw = null;
        if (server.GetString("createdBy") == _fb.Uid)
        {
            // Сам создал сервер - пароль и так известен (лежит в servers/{id}/admin/secret,
            // доступно только создателю по правилам Firestore) - незачем вводить его самому себе.
            var secret = await _fb.GetDocAsync($"servers/{serverId}/admin/secret");
            pw = secret.GetString("password");
        }
        else if (_config.KnownServerPasswords.TryGetValue(serverId, out var cached) &&
            FirebaseClient.Sha256Hex(cached) == server.GetString("joinPasswordHash"))
        {
            pw = cached;
        }
        else
        {
            pw = InputWindow.Ask(this, serverId, $"Пароль сервера '{serverId}':", isPassword: true);
            if (pw == null) return;
            if (FirebaseClient.Sha256Hex(pw) != server.GetString("joinPasswordHash"))
            {
                MessageWindow.Show(this, "Неверный пароль.", "Ошибка");
                return;
            }
            _config.KnownServerPasswords[serverId] = pw;
            _config.Save();
        }

        await _fb.AddJoinedServerAsync(serverId);
        ShowLobby(serverId, pw);
    }

    private void ShowLobby(string serverId, string pw)
    {
        var lobby = new ServerLobbyControl(_fb, _manager, _config, serverId, pw);
        lobby.BackRequested += async () =>
        {
            LobbyHost.Visibility = Visibility.Collapsed;
            LobbyHost.Content = null;
            ServerListPanel.Visibility = Visibility.Visible;
            if (lobby.ChangedSomething) await RefreshServersAsync();
        };
        lobby.ModsRequested += (loader, mcVersion, isAdmin) =>
        {
            var modsPage = new ModsPage(_fb, _manager, serverId, loader, mcVersion, isAdmin);
            modsPage.BackRequested += () => LobbyHost.Content = lobby;
            LobbyHost.Content = modsPage;
        };
        lobby.ResourcePacksRequested += (mcVersion, isAdmin) =>
        {
            var page = new ResourcePacksPage(_fb, _manager, serverId, mcVersion, isAdmin);
            page.BackRequested += () => LobbyHost.Content = lobby;
            LobbyHost.Content = page;
        };
        lobby.DatapacksRequested += (mcVersion, isAdmin) =>
        {
            var page = new DatapacksPage(_fb, _manager, serverId, mcVersion, isAdmin);
            page.BackRequested += () => LobbyHost.Content = lobby;
            LobbyHost.Content = page;
        };
        LobbyHost.Content = lobby;
        ServerListPanel.Visibility = Visibility.Collapsed;
        LobbyHost.Visibility = Visibility.Visible;
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureHostSetup()) return;
        var dlg = new NewServerWindow(_manager) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            await RefreshServersAsync();
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

}
