using System.Windows;
using System.Windows.Controls;
using MinecraftHostApp.Services;

namespace MinecraftHostApp;

public partial class FriendProfileControl : UserControl
{
    public event Action? BackRequested;

    public FriendProfileControl(FirebaseClient fb, string friendName, string friendUid, bool canView)
    {
        InitializeComponent();
        TitleText.Text = $"Профиль: {friendName}";
        Loaded += async (_, _) =>
        {
            var avatarUrl = await fb.GetAvatarUrlAsync(friendUid);
            if (!string.IsNullOrWhiteSpace(avatarUrl)) AvatarEllipse.Fill = await LoadAvatarBrushAsync(avatarUrl);
            await LoadAsync(fb, friendUid, canView);
        };
    }

    private static async Task<System.Windows.Media.Brush> LoadAvatarBrushAsync(string url)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient();
            var bytes = await http.GetByteArrayAsync(url);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            using var ms = new System.IO.MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            var brush = new System.Windows.Media.ImageBrush(bmp) { Stretch = System.Windows.Media.Stretch.UniformToFill };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return System.Windows.Media.Brushes.Transparent;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private async Task LoadAsync(FirebaseClient fb, string friendUid, bool canView)
    {
        if (!canView)
        {
            StatusMessage.Text = "Этот человек скрыл список своих серверов.";
            return;
        }

        var serverIds = await fb.GetJoinedServersAsync(friendUid);
        if (serverIds.Count == 0)
        {
            StatusMessage.Text = "Пока не заходил ни на один сервер.";
            return;
        }

        var cards = new List<ServerCardViewModel>();
        foreach (var id in serverIds)
        {
            var doc = await fb.GetDocAsync($"servers/{id}");
            if (!doc.Exists) continue;
            cards.Add(new ServerCardViewModel
            {
                Id = id,
                LoaderVersion = $"{doc.GetString("loader")} {doc.GetString("mcVersion")}",
            });
        }
        StatusMessage.Text = "";
        ServersList.ItemsSource = cards;
    }
}
