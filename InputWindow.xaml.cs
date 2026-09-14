using System.Windows;

namespace MinecraftHostApp;

public partial class InputWindow : Window
{
    public string Value { get; private set; } = "";

    public InputWindow(string title, string prompt, bool isPassword = false, string defaultValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        if (isPassword)
        {
            PlainInput.Visibility = Visibility.Collapsed;
            PassInput.Visibility = Visibility.Visible;
            PassInput.Password = defaultValue;
            Loaded += (_, _) => PassInput.Focus();
        }
        else
        {
            PlainInput.Text = defaultValue;
            Loaded += (_, _) => { PlainInput.Focus(); PlainInput.SelectAll(); };
        }
    }

    public static string? Ask(Window owner, string title, string prompt, bool isPassword = false, string defaultValue = "")
    {
        var w = new InputWindow(title, prompt, isPassword, defaultValue) { Owner = owner };
        return w.ShowDialog() == true ? w.Value : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Value = PassInput.Visibility == Visibility.Visible ? PassInput.Password : PlainInput.Text;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
