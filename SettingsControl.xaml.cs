using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MinecraftHostApp.Models;
using MinecraftHostApp.Services;

namespace MinecraftHostApp;

// Страница настроек как обычная встроенная страница (не отдельное окно) - чтобы была
// широкая, а не зажатая в узкий попап, и в общем стиле с остальным приложением.
public partial class SettingsControl : UserControl
{
    public AppConfig Config { get; }
    private readonly FirebaseClient? _fb;
    private readonly GoogleDriveClient _googleDrive;

    public event Action? BackRequested;

    private Window? OwnerWindow => Window.GetWindow(this);

    private static readonly (string Value, string Label)[] PrivacyOptions =
    {
        ("everyone", "Все"),
        ("friends", "Только друзья"),
        ("nobody", "Никто"),
    };

    public SettingsControl(AppConfig config, FirebaseClient? fb = null)
    {
        InitializeComponent();
        Config = config;
        _fb = fb;
        _googleDrive = new GoogleDriveClient(config);
        DriveBox.Text = config.SharedFolder;
        PlayitBox.Text = config.PlayitAddress;
        ClientPathBox.Text = config.ClientInstallPath;
        JavaPathBox.Text = config.JavaPath;
        UpdateJavaAutoDetectText();
        UpdateGoogleStatus();

        PrivacyBox.ItemsSource = PrivacyOptions.Select(o => o.Label).ToList();
        PrivacyBox.SelectedIndex = 0;
        if (_fb != null) Loaded += async (_, _) =>
        {
            var current = await _fb.GetServersPrivacyAsync(_fb.Uid!);
            var idx = Array.FindIndex(PrivacyOptions, o => o.Value == current);
            PrivacyBox.SelectedIndex = idx < 0 ? 0 : idx;
        };
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void UpdateGoogleStatus()
    {
        GoogleStatusText.Text = _googleDrive.IsConnected
            ? "Подключено. Приложение может само создавать папки и делиться ими с друзьями."
            : "Не подключено - без этого доступ к папке мира настраивается вручную через сайт Google Drive.";
        GoogleConnectButton.Content = _googleDrive.IsConnected ? "Выйти из Google" : "Войти через Google";
    }

    private async void GoogleConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_googleDrive.IsConnected)
        {
            _googleDrive.Disconnect();
            UpdateGoogleStatus();
            return;
        }

        GoogleConnectButton.IsEnabled = false;
        GoogleStatusText.Text = "Открываю браузер для входа...";
        try
        {
            var result = await _googleDrive.ConnectAsync();
            GoogleStatusText.Text = result;
        }
        catch (Exception ex)
        {
            GoogleStatusText.Text = $"Не удалось войти: {ex.Message}";
        }
        finally
        {
            GoogleConnectButton.IsEnabled = true;
            UpdateGoogleStatus();
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выбери папку с миром на Google Drive"
        };
        if (dialog.ShowDialog(OwnerWindow) == true)
        {
            DriveBox.Text = dialog.FolderName;
        }
    }

    private void BrowseClient_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Куда ставить игровой клиент"
        };
        if (dialog.ShowDialog(OwnerWindow) == true)
        {
            ClientPathBox.Text = dialog.FolderName;
        }
    }

    private void UpdateJavaAutoDetectText()
    {
        if (!string.IsNullOrWhiteSpace(JavaPathBox.Text)) { JavaAutoDetectText.Text = ""; return; }
        var found = JavaLocator.Find();
        JavaAutoDetectText.Text = found != null
            ? $"Найдена сама: {found}"
            : "Java не найдена автоматически - установи Java (Minecraft 1.20.5+ требует Java 21) или укажи путь вручную.";
    }

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Выбери java.exe", Filter = "java.exe|java.exe" };
        if (dialog.ShowDialog(OwnerWindow) == true)
        {
            JavaPathBox.Text = dialog.FileName;
            UpdateJavaAutoDetectText();
        }
    }

    private void CheckDrive_Click(object sender, RoutedEventArgs e)
    {
        // Раньше проверяли только Directory.Exists папки - это давало "установлен", даже если Drive
        // не запущен и человек не вошёл в аккаунт (папка остаётся на диске и без реальной работы).
        // Настоящий признак, что Drive реально работает прямо сейчас - его процесс в памяти.
        var running = Process.GetProcessesByName("GoogleDriveFS").Length > 0;
        var folderExists = Directory.Exists(@"C:\Program Files\Google\Drive File Stream");

        if (running)
        {
            MessageWindow.Show(OwnerWindow,
                "Google Drive для компьютеров установлен и запущен.\n\n" +
                "Если папки с миром всё равно не видно в 'Обзор' - проверь, что ты вошёл именно в тот " +
                "аккаунт, которому расшарили папку, и что приглашение принято (загляни на drive.google.com " +
                "под этим аккаунтом - папка должна быть видна там).",
                "Проверка");
            return;
        }

        if (folderExists)
        {
            var launchBat = @"C:\Program Files\Google\Drive File Stream\launch.bat";
            var launched = false;
            if (File.Exists(launchBat))
            {
                try { Process.Start(new ProcessStartInfo(launchBat) { UseShellExecute = true }); launched = true; }
                catch { /* не критично - просто не смогли запустить автоматически */ }
            }
            MessageWindow.Show(OwnerWindow,
                (launched ? "Google Drive установлен, но не был запущен - запускаю.\n\n" : "Google Drive установлен, но сейчас не запущен. Открой его вручную (Пуск -> Google Drive).\n\n") +
                "После запуска войди в тот аккаунт, которому расшарили папку с миром, дождись синхронизации " +
                "и только потом нажимай 'Обзор'.",
                "Проверка");
            return;
        }

        var result = MessageWindow.Confirm(OwnerWindow,
            "Google Drive для компьютеров не найден. Установить сейчас?",
            "Google Drive");
        if (!result) return;

        try
        {
            var psi = new ProcessStartInfo("winget",
                "install --id Google.GoogleDrive --silent --accept-package-agreements --accept-source-agreements")
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit();

            MessageWindow.Show(OwnerWindow,
                "Google Drive установлен. Дальше сделай по шагам:\n\n" +
                "1. Открой программу 'Google Drive' (Пуск -> Google Drive).\n" +
                "2. Нажми 'Войти' и введи тот аккаунт, на который тебе прислали\n" +
                "   ссылку на папку с миром.\n" +
                "3. После входа в проводнике слева появится новый диск (обычно G:).\n" +
                "4. Открой его, найди папку с миром (её тебе должны были расшарить),\n" +
                "   и нажми 'Обзор...' здесь, чтобы указать её.",
                "Google Drive установлен");
        }
        catch (Exception ex)
        {
            MessageWindow.Show(OwnerWindow, $"Не получилось установить автоматически: {ex.Message}\n\n" +
                "Установи вручную с https://www.google.com/drive/download/", "Ошибка");
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DriveBox.Text) || string.IsNullOrWhiteSpace(PlayitBox.Text))
        {
            StatusText.Text = "Заполни папку Drive и адрес playit.";
            return;
        }

        Config.SharedFolder = DriveBox.Text.Trim();
        Config.PlayitAddress = PlayitBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(ClientPathBox.Text)) Config.ClientInstallPath = ClientPathBox.Text.Trim();
        Config.JavaPath = JavaPathBox.Text.Trim();
        Config.Save();

        if (_fb != null)
        {
            var idx = PrivacyBox.SelectedIndex < 0 ? 0 : PrivacyBox.SelectedIndex;
            await _fb.SetServersPrivacyAsync(PrivacyOptions[idx].Value);
        }

        StatusText.Text = "Сохранено.";
    }
}
