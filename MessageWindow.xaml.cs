using System.Windows;

namespace MinecraftHostApp;

// Тёмная замена стандартному MessageBox (у которого белый попап не вписывается в оформление приложения).
public partial class MessageWindow : Window
{
    public MessageWindow(string title, string message, bool isConfirm)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        if (isConfirm)
        {
            NoButton.Visibility = Visibility.Visible;
            YesOkButton.Content = "Да";
        }
    }

    public static void Show(Window? owner, string message, string title = "Сообщение")
    {
        var w = new MessageWindow(title, message, isConfirm: false);
        if (owner != null) w.Owner = owner;
        w.ShowDialog();
    }

    public static bool Confirm(Window? owner, string message, string title = "Подтверждение")
    {
        var w = new MessageWindow(title, message, isConfirm: true);
        if (owner != null) w.Owner = owner;
        return w.ShowDialog() == true;
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
