using System.Windows;
using System.Windows.Controls;
using MinecraftHostApp.Services;

namespace MinecraftHostApp;

public partial class NewServerWindow : Window
{
    private readonly ServerManager _manager;
    public bool Created { get; private set; }

    public NewServerWindow(ServerManager manager)
    {
        InitializeComponent();
        _manager = manager;
        Loaded += NewServerWindow_Loaded;
    }

    private async void NewServerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        VersionBox.IsEnabled = false;
        StatusText.Text = "Загружаю список версий Minecraft...";
        try
        {
            var versions = await _manager.GetMojangReleaseVersionsAsync();
            VersionBox.ItemsSource = versions;
            VersionBox.SelectedIndex = 0;
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Не удалось загрузить список версий: {ex.Message}";
        }
        VersionBox.IsEnabled = true;
        UpdateLoaderHint();
    }

    private void LoaderBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateLoaderHint();

    private void UpdateLoaderHint()
    {
        if (VersionHint == null || LoaderBox.SelectedItem == null) return;
        var loader = ((ComboBoxItem)LoaderBox.SelectedItem).Content.ToString();
        VersionHint.Text = loader == "forge"
            ? "Forge пока не поддерживается приложением - используй scripts\\new-server.ps1"
            : "Сервер и файлы для этой версии/загрузчика скачаются автоматически.";
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var id = NameBox.Text.Trim();
        var loader = ((ComboBoxItem)LoaderBox.SelectedItem).Content.ToString()!;
        var version = VersionBox.SelectedItem?.ToString() ?? "";
        var password = PasswordBox.Text;

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(version))
        {
            StatusText.Text = "Название, версия и пароль обязательны.";
            return;
        }

        CreateButton.IsEnabled = false;
        var progress = new Progress<string>(msg => StatusText.Text = msg);

        try
        {
            string error = loader switch
            {
                "vanilla" => await _manager.CreateVanillaServerAsync(id, version, progress),
                "fabric" => await _manager.CreateFabricServerAsync(id, version, progress),
                "forge" => "Forge пока не реализован в приложении - используй new-server.ps1 -Loader forge из scripts\\.",
                _ => "Неизвестный загрузчик."
            };
            if (!string.IsNullOrEmpty(error))
            {
                StatusText.Text = error;
                CreateButton.IsEnabled = true;
                return;
            }

            StatusText.Text = "Регистрирую сервер в Firebase...";
            await _manager.RegisterServerAsync(id, loader, version, password, HiddenBox.IsChecked == true);

            StatusText.Text = "Загружаю файлы сервера в общую папку (чтобы другие тоже могли хостить)...";
            await _manager.SyncServerFilesToCloudAsync(id);

            Created = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
            CreateButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
