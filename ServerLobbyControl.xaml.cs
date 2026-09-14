using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MinecraftHostApp.Models;
using MinecraftHostApp.Services;

namespace MinecraftHostApp;

public partial class ServerLobbyControl : UserControl
{
    private readonly FirebaseClient _fb;
    private readonly ServerManager _manager;
    private readonly AppConfig _config;
    private readonly string _serverId;
    private string _password;

    private FirestoreDoc _server = new();
    private FirestoreDoc _session = new();
    private string _currentAddress = "";
    private bool _isAdmin;
    private bool _isBanned;
    private bool _canHost;
    private bool _isHostingMe;

    public bool ChangedSomething { get; private set; }
    public event Action? BackRequested;
    public event Action<string, string, bool>? ModsRequested; // loader, mcVersion, isAdmin
    public event Action<string, bool>? ResourcePacksRequested; // mcVersion, isAdmin
    public event Action<string, bool>? DatapacksRequested; // mcVersion, isAdmin

    private Window? OwnerWindow => Window.GetWindow(this);

    private static readonly Brush FreeColor = new SolidColorBrush(Color.FromRgb(0x5F, 0xBF, 0x6B));
    private static readonly Brush BusyColor = new SolidColorBrush(Color.FromRgb(0xE5, 0x60, 0x60));

    private bool _ramSliderInitializing;
    private int _totalRamMb;

    public ServerLobbyControl(FirebaseClient fb, ServerManager manager, AppConfig config, string serverId, string password)
    {
        InitializeComponent();
        _fb = fb; _manager = manager; _config = config; _serverId = serverId; _password = password;

        _totalRamMb = SystemInfo.GetTotalPhysicalMemoryMb();
        _ramSliderInitializing = true;
        RamSlider.Minimum = 1024;
        RamSlider.Maximum = Math.Max(2048, (int)(_totalRamMb * 0.9));
        RamSlider.TickFrequency = 256;
        RamSlider.IsSnapToTickEnabled = true;
        // По умолчанию (пока не трогали ползунок) - 30% от общей памяти ПК, как в лаунчерах Minecraft.
        var defaultMb = Math.Min(RamSlider.Maximum, Math.Round(_totalRamMb * 0.3 / 256.0) * 256);
        RamSlider.Value = _config.ServerRamMb > 0 ? Math.Min(_config.ServerRamMb, RamSlider.Maximum) : defaultMb;
        _ramSliderInitializing = false;
        UpdateRamValueText();

        _manager.ServerOutput += OnServerOutput;
        Unloaded += (_, _) => _manager.ServerOutput -= OnServerOutput;

        Loaded += async (_, _) => await ReloadAsync();
    }

    // OutputDataReceived сервера прилетает в фоновом потоке - обновлять UI можно только через Dispatcher.
    private void OnServerOutput(string serverId, string line)
    {
        if (serverId != _serverId) return;
        Dispatcher.Invoke(() =>
        {
            ConsoleText.Text += line + "\n";
            const int maxChars = 20000;
            if (ConsoleText.Text.Length > maxChars) ConsoleText.Text = ConsoleText.Text[^maxChars..];
            ConsoleScroll.ScrollToEnd();
        });
    }

    private void SendConsoleCommand_Click(object sender, RoutedEventArgs e) => SendConsoleCommand();

    private void ConsoleCommandBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) SendConsoleCommand();
    }

    private void SendConsoleCommand()
    {
        var cmd = ConsoleCommandBox.Text.Trim();
        if (string.IsNullOrEmpty(cmd)) return;
        if (_manager.SendCommand(_serverId, cmd))
        {
            ConsoleText.Text += $"> {cmd}\n";
            ConsoleScroll.ScrollToEnd();
        }
        else
        {
            StatusMessage.Text = "Сервер сейчас не запущен с этого ПК - команду отправить некуда.";
        }
        ConsoleCommandBox.Clear();
    }

    private void UpdateRamValueText()
    {
        var mb = (int)RamSlider.Value;
        var percent = _totalRamMb > 0 ? (int)Math.Round(mb * 100.0 / _totalRamMb) : 0;
        RamValueText.Text = $"{mb / 1024.0:0.#} ГБ ({percent}% от {_totalRamMb / 1024.0:0.#} ГБ)";
    }

    private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateRamValueText();
        if (_ramSliderInitializing) return;
        _config.ServerRamMb = (int)RamSlider.Value;
        _config.Save();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void Mods_Click(object sender, RoutedEventArgs e) =>
        ModsRequested?.Invoke(_server.GetString("loader"), _server.GetString("mcVersion"), _isAdmin);

    private void ResourcePacks_Click(object sender, RoutedEventArgs e) =>
        ResourcePacksRequested?.Invoke(_server.GetString("mcVersion"), _isAdmin);

    private void Datapacks_Click(object sender, RoutedEventArgs e) =>
        DatapacksRequested?.Invoke(_server.GetString("mcVersion"), _isAdmin);

    private async Task ReloadAsync()
    {
        _server = await _fb.GetDocAsync($"servers/{_serverId}");
        _session = await _fb.GetDocAsync($"sessions/{_serverId}");

        TitleText.Text = _serverId;
        LoaderVersionText.Text = $"{_server.GetString("loader")} {_server.GetString("mcVersion")}";

        var authorUid = _server.GetString("createdBy");
        AuthorText.Text = await _fb.GetPublicDisplayNameAsync(authorUid);
        _isAdmin = authorUid == _fb.Uid;

        var allowedHosts = _server.GetArray("allowedHosts");
        var names = new List<string>();
        foreach (var uid in allowedHosts) names.Add(await _fb.GetPublicDisplayNameAsync(uid));
        AllowedHostsText.Text = names.Count > 0 ? string.Join(", ", names) : "(никто)";

        _isBanned = _server.GetArray("bannedUids").Contains(_fb.Uid!);
        _canHost = allowedHosts.Contains(_fb.Uid!);

        bool isHosting = _session.Exists && _session.GetString("status") == "hosting";
        _isHostingMe = isHosting && _session.GetString("hostUid") == _fb.Uid;

        StatusText.Text = isHosting ? "Хостит кто-то" : "Свободен";
        StatusText.Foreground = isHosting ? BusyColor : FreeColor;
        StatusDot.Fill = isHosting ? BusyColor : FreeColor;

        AddressText.Visibility = Visibility.Collapsed;
        AddressCopiedHint.Visibility = Visibility.Collapsed;
        if (isHosting)
        {
            var hash = FirebaseClient.Sha256Hex(_password);
            var access = await _fb.GetDocAsync($"sessions/{_serverId}/access/{hash}");
            if (access.Exists && !string.IsNullOrEmpty(access.GetString("address")))
            {
                _currentAddress = access.GetString("address");
                AddressText.Text = $"Адрес: {_currentAddress} (нажми, чтобы скопировать)";
                AddressText.Visibility = Visibility.Visible;
            }
        }

        if (_isBanned)
        {
            ActionButton.Content = "Тебя забанили на этом сервере";
            ActionButton.IsEnabled = false;
        }
        else if (isHosting && _isHostingMe)
        {
            ActionButton.Content = "Я закончил - сохранить мир и освободить";
            ActionButton.IsEnabled = true;
        }
        else if (isHosting)
        {
            ActionButton.Content = "Скопировать адрес в буфер";
            ActionButton.IsEnabled = true;
        }
        else if (_canHost)
        {
            ActionButton.Content = "Стать хостом";
            ActionButton.IsEnabled = true;
        }
        else
        {
            ActionButton.Content = "Нет прав на хостинг";
            ActionButton.IsEnabled = false;
        }

        AdminPanel.Visibility = _isAdmin ? Visibility.Visible : Visibility.Collapsed;
        // Пока только у создателя (как и раньше) - позже можно дать возможность явно назначать
        // конкретных людей, кому создатель разрешил менять настройки, отдельно от права хостить.
        HostSettingsPanel.Visibility = _isAdmin ? Visibility.Visible : Visibility.Collapsed;
        ModsButton.Visibility = _server.GetString("loader") == "vanilla" ? Visibility.Collapsed : Visibility.Visible;
        RamPanel.Visibility = !isHosting && _canHost && !_isBanned ? Visibility.Visible : Visibility.Collapsed;
        ConsolePanel.Visibility = _isHostingMe && _manager.IsServerRunningHere(_serverId) ? Visibility.Visible : Visibility.Collapsed;

        ShareDriveHint.Text = _manager.IsDriveConnected
            ? "Google Drive подключён - можно давать доступ по email."
            : "Google Drive не подключён - зайди через Google в Настройках, чтобы давать доступ прямо отсюда.";

        OnlineModeCheck.IsChecked = _server.GetString("onlineMode") == "true";
        MaxPlayersBox.Text = string.IsNullOrEmpty(_server.GetString("maxPlayers")) ? "20" : _server.GetString("maxPlayers");

        GameModeBox.ItemsSource = GameModeOptions.Select(o => o.Label).ToList();
        GameModeBox.SelectedIndex = Math.Max(0, Array.FindIndex(GameModeOptions, o => o.Value == _server.GetString("gamemode")));
        DifficultyBox.ItemsSource = DifficultyOptions.Select(o => o.Label).ToList();
        DifficultyBox.SelectedIndex = Math.Max(0, Array.FindIndex(DifficultyOptions, o => o.Value == _server.GetString("difficulty")));

        SpawnProtectionBox.Text = string.IsNullOrEmpty(_server.GetString("spawnProtection")) ? "16" : _server.GetString("spawnProtection");
        WhiteListCheck.IsChecked = _server.GetString("whiteList") == "true";
        AllowFlightCheck.IsChecked = _server.GetString("allowFlight") == "true";
        ForceGamemodeCheck.IsChecked = _server.GetString("forceGamemode") == "true";
        ResourcePackBox.Text = _server.GetString("resourcePack");
        RequireResourcePackCheck.IsChecked = _server.GetString("requireResourcePack") == "true";
        ResourcePackPromptBox.Text = _server.GetString("resourcePackPrompt");
    }

    private static readonly (string Value, string Label)[] GameModeOptions =
    {
        ("survival", "Выживание"), ("creative", "Творческий"), ("adventure", "Приключение"), ("spectator", "Наблюдатель"),
    };
    private static readonly (string Value, string Label)[] DifficultyOptions =
    {
        ("peaceful", "Мирно"), ("easy", "Легко"), ("normal", "Нормально"), ("hard", "Сложно"),
    };

    // Собирает server.properties из значений, сохранённых в Firestore (или дефолтов) - вызывается
    // перед каждым запуском сервера, независимо от того, кто сейчас хостит.
    private Dictionary<string, string> BuildServerProperties()
    {
        var gamemode = GameModeOptions[Math.Max(0, Array.FindIndex(GameModeOptions, o => o.Value == _server.GetString("gamemode")))].Value;
        var difficulty = DifficultyOptions[Math.Max(0, Array.FindIndex(DifficultyOptions, o => o.Value == _server.GetString("difficulty")))].Value;
        return new Dictionary<string, string>
        {
            ["online-mode"] = _server.GetString("onlineMode") == "true" ? "true" : "false",
            ["max-players"] = string.IsNullOrEmpty(_server.GetString("maxPlayers")) ? "20" : _server.GetString("maxPlayers"),
            ["gamemode"] = gamemode,
            ["difficulty"] = difficulty,
            ["spawn-protection"] = string.IsNullOrEmpty(_server.GetString("spawnProtection")) ? "16" : _server.GetString("spawnProtection"),
            ["white-list"] = _server.GetString("whiteList") == "true" ? "true" : "false",
            ["allow-flight"] = _server.GetString("allowFlight") == "true" ? "true" : "false",
            ["force-gamemode"] = _server.GetString("forceGamemode") == "true" ? "true" : "false",
            ["resource-pack"] = _server.GetString("resourcePack"),
            ["require-resource-pack"] = _server.GetString("requireResourcePack") == "true" ? "true" : "false",
            ["resource-pack-prompt"] = _server.GetString("resourcePackPrompt"),
        };
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_isBanned) return;

        bool isHosting = _session.Exists && _session.GetString("status") == "hosting";

        if (isHosting && _isHostingMe)
        {
            var (ok, msg) = await _manager.PackAndReleaseAsync(_serverId, _password);
            StatusMessage.Text = msg;
            ChangedSomething = true;
            await ReloadAsync();
            return;
        }

        if (isHosting)
        {
            var hash = FirebaseClient.Sha256Hex(_password);
            var access = await _fb.GetDocAsync($"sessions/{_serverId}/access/{hash}");
            if (access.Exists) Clipboard.SetText(access.GetString("address"));
            StatusMessage.Text = "Адрес скопирован в буфер.";
            return;
        }

        if (!_config.IsHostReady())
        {
            MessageWindow.Show(OwnerWindow, "Сначала настрой папку Drive и playit в Настройках.", "Нужна настройка");
            return;
        }

        var port = _manager.GetServerPort(_serverId);
        var portNote = ServerManager.IsPlayitRunning()
            ? $"Порт сервера: {port}. playit.gg запущен - убедись, что его туннель настроен именно на этот порт."
            : $"⚠ playit.gg сейчас не запущен на этом ПК! Запусти его и настрой туннель на порт {port} " +
              "(это порт твоего сервера) - иначе друзья по адресу не подключатся.";

        if (!MessageWindow.Confirm(OwnerWindow, $"Стать хостом сервера '{_serverId}'?\n\n{portNote}", "Подтверждение")) return;

        var (claimOk, claimMsg) = await _manager.ClaimAndUnpackAsync(_serverId, _password, _server.GetString("loader"), _server.GetString("mcVersion"));
        if (!claimOk) { StatusMessage.Text = claimMsg; return; }

        // Настройки сервера - по умолчанию пиратка (поле не задано в Firestore -> false), т.к. большинство
        // друзей не с лицензии. Меняются в панели админа ("Настройки сервера").
        _manager.ApplyServerSettings(_serverId, BuildServerProperties());

        _manager.StartServerProcess(_serverId, _config.ServerRamMb);
        Clipboard.SetText(_config.PlayitAddress);
        StatusMessage.Text = $"{claimMsg}\nСервер запускается, консоль появится ниже. Адрес скопирован в буфер.";
        ChangedSomething = true;
        await ReloadAsync();
    }

    private async void DownloadClient_Click(object sender, RoutedEventArgs e)
    {
        var confirmDownload = MessageWindow.Confirm(OwnerWindow,
            $"Скачать файлы клиента для '{_serverId}' в {System.IO.Path.Combine(_config.ClientInstallPath, _serverId)}?",
            "Установка клиента");
        if (!confirmDownload) return;
        try
        {
            var result = await _manager.DownloadClientAsync(_serverId, _server.GetString("loader"), _server.GetString("mcVersion"), _config.ClientInstallPath, new Progress<string>());
            MessageWindow.Show(OwnerWindow, result, "Готово");
        }
        catch (Exception ex)
        {
            MessageWindow.Show(OwnerWindow, $"Ошибка: {ex.Message}", "Ошибка");
        }
    }

    private async void ShowPassword_Click(object sender, RoutedEventArgs e)
    {
        var secret = await _fb.GetDocAsync($"servers/{_serverId}/admin/secret");
        MessageWindow.Show(OwnerWindow, $"Пароль сервера: {secret.GetString("password")}", "Пароль");
    }

    private async void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        var newPw = InputWindow.Ask(OwnerWindow, "Новый пароль", "Введи новый пароль для сервера:");
        if (string.IsNullOrWhiteSpace(newPw)) return;
        var hash = FirebaseClient.Sha256Hex(newPw);
        await _fb.SetDocAsync($"servers/{_serverId}", new Dictionary<string, object> { ["joinPasswordHash"] = hash });
        await _fb.SetDocAsync($"servers/{_serverId}/admin/secret", new Dictionary<string, object> { ["password"] = newPw });
        _password = newPw;
        _config.KnownServerPasswords[_serverId] = newPw;
        _config.Save();
        StatusMessage.Text = "Пароль обновлён.";
        ChangedSomething = true;
    }

    private async void AddHost_Click(object sender, RoutedEventArgs e)
    {
        var uid = InputWindow.Ask(OwnerWindow, "Разрешить хостить", "UID друга:");
        if (string.IsNullOrWhiteSpace(uid)) return;
        var current = _server.GetArray("allowedHosts").ToList();
        if (!current.Contains(uid)) current.Add(uid);
        await _fb.SetDocAsync($"servers/{_serverId}", new Dictionary<string, object> { ["allowedHosts"] = current });
        ChangedSomething = true;
        await ReloadAsync();
    }

    private async void RemoveHost_Click(object sender, RoutedEventArgs e)
    {
        var uid = InputWindow.Ask(OwnerWindow, "Забрать право хоста", "UID:");
        if (string.IsNullOrWhiteSpace(uid)) return;
        var current = _server.GetArray("allowedHosts").Where(u => u != uid).ToList();
        await _fb.SetDocAsync($"servers/{_serverId}", new Dictionary<string, object> { ["allowedHosts"] = current });
        ChangedSomething = true;
        await ReloadAsync();
    }

    private async void Ban_Click(object sender, RoutedEventArgs e)
    {
        var uid = InputWindow.Ask(OwnerWindow, "Забанить навсегда", "UID:");
        if (string.IsNullOrWhiteSpace(uid)) return;
        var current = _server.GetArray("bannedUids").ToList();
        if (!current.Contains(uid)) current.Add(uid);
        await _fb.SetDocAsync($"servers/{_serverId}", new Dictionary<string, object> { ["bannedUids"] = current });
        StatusMessage.Text = $"{uid} забанен навсегда.";
        ChangedSomething = true;
        await ReloadAsync();
    }

    private async void Unban_Click(object sender, RoutedEventArgs e)
    {
        var uid = InputWindow.Ask(OwnerWindow, "Разбанить", "UID:");
        if (string.IsNullOrWhiteSpace(uid)) return;
        var current = _server.GetArray("bannedUids").Where(u => u != uid).ToList();
        await _fb.SetDocAsync($"servers/{_serverId}", new Dictionary<string, object> { ["bannedUids"] = current });
        StatusMessage.Text = $"{uid} разбанен.";
        ChangedSomething = true;
        await ReloadAsync();
    }

    private async void Kick_Click(object sender, RoutedEventArgs e)
    {
        var uid = InputWindow.Ask(OwnerWindow, "Кикнуть на время", "UID:");
        if (string.IsNullOrWhiteSpace(uid)) return;
        var minutesStr = InputWindow.Ask(OwnerWindow, "На сколько минут", "Минут:", defaultValue: "30");
        if (!int.TryParse(minutesStr, out var minutes)) minutes = 30;

        var until = DateTime.UtcNow.AddMinutes(minutes).ToString("o");
        var current = _server.GetArray("kickedUntil").Where(s => !s.StartsWith(uid + "|")).ToList();
        current.Add($"{uid}|{until}");
        await _fb.SetDocAsync($"servers/{_serverId}", new Dictionary<string, object> { ["kickedUntil"] = current });
        StatusMessage.Text = $"{uid} кикнут на {minutes} мин. (снимается автоматически по времени в приложении).";
        ChangedSomething = true;
    }

    private void AddressText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentAddress)) return;
        Clipboard.SetText(_currentAddress);
        AddressCopiedHint.Text = "Скопировано!";
        AddressCopiedHint.Visibility = Visibility.Visible;
    }

    private async void ShareDrive_Click(object sender, RoutedEventArgs e)
    {
        if (!_manager.IsDriveConnected)
        {
            MessageWindow.Show(OwnerWindow, "Сначала войди через Google в Настройках - тогда можно будет давать доступ прямо отсюда.", "Нужен вход в Google");
            return;
        }
        var email = InputWindow.Ask(OwnerWindow, "Дать доступ к папке мира", "Email друга в Google:");
        if (string.IsNullOrWhiteSpace(email)) return;

        ShareDriveButton.IsEnabled = false;
        try
        {
            await _manager.ShareServerFolderAsync(_serverId, email.Trim());
            ShareDriveHint.Text = $"{email} получил доступ к папке мира на Drive.";
        }
        catch (Exception ex)
        {
            ShareDriveHint.Text = $"Не удалось выдать доступ: {ex.Message}";
        }
        finally
        {
            ShareDriveButton.IsEnabled = true;
        }
    }

    private void AddWhitelist_Click(object sender, RoutedEventArgs e)
    {
        var username = InputWindow.Ask(OwnerWindow, "Белый список", "Ник в Minecraft (не UID приложения):");
        if (string.IsNullOrWhiteSpace(username)) return;
        var appliedNow = _manager.AddWhitelist(_serverId, username.Trim());
        StatusMessage.Text = appliedNow
            ? $"{username} добавлен в белый список прямо сейчас."
            : $"{username} будет в белом списке при следующем запуске сервера (сейчас сервер не запущен с этого ПК).";
    }

    private void RemoveWhitelist_Click(object sender, RoutedEventArgs e)
    {
        var username = InputWindow.Ask(OwnerWindow, "Белый список", "Ник в Minecraft:");
        if (string.IsNullOrWhiteSpace(username)) return;
        var appliedNow = _manager.RemoveWhitelist(_serverId, username.Trim());
        StatusMessage.Text = appliedNow
            ? $"{username} убран из белого списка прямо сейчас."
            : $"{username} будет убран из белого списка при следующем запуске сервера (сейчас сервер не запущен с этого ПК).";
    }

    private void AddOp_Click(object sender, RoutedEventArgs e)
    {
        var username = InputWindow.Ask(OwnerWindow, "Выдать OP", "Ник в Minecraft (не UID приложения):");
        if (string.IsNullOrWhiteSpace(username)) return;
        var appliedNow = _manager.AddOperator(_serverId, username.Trim());
        StatusMessage.Text = appliedNow
            ? $"{username} получил OP прямо сейчас."
            : $"{username} получит OP при следующем запуске сервера (сейчас сервер не запущен с этого ПК).";
    }

    private void RemoveOp_Click(object sender, RoutedEventArgs e)
    {
        var username = InputWindow.Ask(OwnerWindow, "Забрать OP", "Ник в Minecraft:");
        if (string.IsNullOrWhiteSpace(username)) return;
        var appliedNow = _manager.RemoveOperator(_serverId, username.Trim());
        StatusMessage.Text = appliedNow
            ? $"{username} лишён OP прямо сейчас."
            : $"{username} лишится OP при следующем запуске сервера (сейчас сервер не запущен с этого ПК).";
    }

    private async void SaveServerSettings_Click(object sender, RoutedEventArgs e)
    {
        var maxPlayers = int.TryParse(MaxPlayersBox.Text.Trim(), out var mp) ? mp : 20;
        var spawnProtection = int.TryParse(SpawnProtectionBox.Text.Trim(), out var sp) ? sp : 16;
        var gamemode = GameModeOptions[Math.Max(0, GameModeBox.SelectedIndex)].Value;
        var difficulty = DifficultyOptions[Math.Max(0, DifficultyBox.SelectedIndex)].Value;

        await _fb.SetDocAsync($"servers/{_serverId}", new Dictionary<string, object>
        {
            ["onlineMode"] = OnlineModeCheck.IsChecked == true ? "true" : "false",
            ["maxPlayers"] = maxPlayers.ToString(),
            ["gamemode"] = gamemode,
            ["difficulty"] = difficulty,
            ["spawnProtection"] = spawnProtection.ToString(),
            ["whiteList"] = WhiteListCheck.IsChecked == true ? "true" : "false",
            ["allowFlight"] = AllowFlightCheck.IsChecked == true ? "true" : "false",
            ["forceGamemode"] = ForceGamemodeCheck.IsChecked == true ? "true" : "false",
            ["resourcePack"] = ResourcePackBox.Text.Trim(),
            ["requireResourcePack"] = RequireResourcePackCheck.IsChecked == true ? "true" : "false",
            ["resourcePackPrompt"] = ResourcePackPromptBox.Text.Trim(),
        });
        StatusMessage.Text = "Настройки сохранены. Применятся при следующем запуске сервера.";
        ChangedSomething = true;
        await ReloadAsync();
    }

    private async void UploadWorldZip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Выбери .zip с миром", Filter = "Zip-архив|*.zip" };
        if (dlg.ShowDialog(OwnerWindow) != true) return;
        await UploadWorldAsync(dlg.FileName);
    }

    private void UploadWorldFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Выбери папку мира (там, где level.dat)" };
        if (dlg.ShowDialog(OwnerWindow) != true) return;
        _ = UploadWorldAsync(dlg.FolderName);
    }

    private async Task UploadWorldAsync(string sourcePath)
    {
        if (!MessageWindow.Confirm(OwnerWindow,
                $"Загрузить этот мир для сервера '{_serverId}'? Он станет текущим при следующем запуске сервера " +
                "(старые версии мира остаются в архиве, никуда не денутся).",
                "Загрузка мира"))
            return;

        StatusMessage.Text = "Загружаю мир...";
        try
        {
            await _manager.UploadOwnWorldAsync(_serverId, sourcePath);
            StatusMessage.Text = "Мир загружен и станет текущим при следующем запуске сервера.";
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"Не удалось загрузить мир: {ex.Message}";
        }
    }

    private async void ResetWorld_Click(object sender, RoutedEventArgs e)
    {
        var confirmReset = MessageWindow.Confirm(OwnerWindow,
            $"Пересоздать мир сервера '{_serverId}'? Текущий мир будет заменён новым при следующем запуске.",
            "Пересоздание мира");
        if (!confirmReset) return;

        var archive = MessageWindow.Confirm(OwnerWindow,
            "Сохранить старый мир в архив на Google Drive (папка 'archived_worlds'), на случай если понадобится вернуть?\n\n" +
            "Если он не нужен - выбери 'Нет', и он будет удалён насовсем.",
            "Сохранить старый мир?");

        StatusMessage.Text = "Пересоздаю мир...";
        var result = await _manager.ResetWorldAsync(_serverId, archive);
        StatusMessage.Text = result;
        ChangedSomething = true;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var confirmDelete = MessageWindow.Confirm(OwnerWindow,
            $"Удалить сервер '{_serverId}' из списка? Мир на Google Drive и локальные файлы НЕ удаляются.",
            "Подтверждение удаления");
        if (!confirmDelete) return;

        await _fb.DeleteDocAsync($"servers/{_serverId}");
        ChangedSomething = true;
        BackRequested?.Invoke();
    }
}
