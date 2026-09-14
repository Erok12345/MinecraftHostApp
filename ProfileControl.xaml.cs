using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftHostApp.Models;
using MinecraftHostApp.Services;

namespace MinecraftHostApp;

public class ProfileServerEntry
{
    public string Id { get; set; } = "";
    public string Subtitle { get; set; } = "";
}

// Отдельная страница профиля - аватар, ник, UID и список своих серверов (создал/заходил).
public partial class ProfileControl : UserControl
{
    private readonly FirebaseClient _fb;
    private readonly AppConfig _config;

    public event Action? BackRequested;
    public event Action<string>? AvatarChanged; // новый url аватара
    public event Action<string>? NicknameChanged; // новый ник

    private Window? OwnerWindow => Window.GetWindow(this);

    public ProfileControl(FirebaseClient fb, AppConfig config, string avatarUrl, string nickname)
    {
        InitializeComponent();
        _fb = fb;
        _config = config;
        if (!string.IsNullOrWhiteSpace(avatarUrl)) SetAvatarImage(avatarUrl);
        NicknameText.Text = nickname;
        UidText.Text = $"UID: {fb.Uid}";
        Loaded += async (_, _) => await LoadServersAsync();
    }

    private async Task LoadServersAsync()
    {
        var allServers = await _fb.ListCollectionAsync("servers");
        var created = allServers.Where(s => s.Doc.GetString("createdBy") == _fb.Uid).ToList();
        CreatedEmptyText.Visibility = created.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CreatedServersList.ItemsSource = created.Select(s => new ProfileServerEntry
        {
            Id = s.Id,
            Subtitle = $"{s.Doc.GetString("loader")} {s.Doc.GetString("mcVersion")}",
        }).ToList();

        // joinedServers - это просто список id, куда человек когда-либо заходил, и никогда не чистится
        // при удалении сервера - сверяем с реальным списком серверов, чтобы удалённые не показывались.
        var joinedIds = await _fb.GetJoinedServersAsync(_fb.Uid!);
        var joinedOnly = allServers
            .Where(s => joinedIds.Contains(s.Id) && s.Doc.GetString("createdBy") != _fb.Uid)
            .Select(s => new ProfileServerEntry { Id = s.Id, Subtitle = $"{s.Doc.GetString("loader")} {s.Doc.GetString("mcVersion")}" })
            .ToList();
        JoinedEmptyText.Visibility = joinedOnly.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        JoinedServersList.ItemsSource = joinedOnly;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

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

    private async void ChangeAvatar_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.webp;*.gif" };
        if (dlg.ShowDialog(OwnerWindow) != true) return;

        try
        {
            StatusText.Text = "Загружаю...";
            var bytes = await File.ReadAllBytesAsync(dlg.FileName);
            var imgbb = new ImgBbClient(_config.ImgBbApiKey);
            var url = await imgbb.UploadAsync(bytes);
            await _fb.SetAvatarUrlAsync(url);
            SetAvatarImage(url);
            AvatarChanged?.Invoke(url);
            StatusText.Text = "Фото обновлено.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Не удалось загрузить фото: {ex.Message}";
        }
    }

    private async void ChangeNickname_Click(object sender, RoutedEventArgs e)
    {
        var nick = InputWindow.Ask(OwnerWindow, "Ник", "Новый никнейм:", defaultValue: NicknameText.Text);
        if (string.IsNullOrWhiteSpace(nick)) return;
        await _fb.SetDisplayNameAsync(nick.Trim());
        NicknameText.Text = nick.Trim();
        NicknameChanged?.Invoke(nick.Trim());
        StatusText.Text = "Ник обновлён.";
    }

    private void CopyUid_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_fb.Uid)) Clipboard.SetText(_fb.Uid);
        StatusText.Text = "UID скопирован в буфер.";
    }
}
