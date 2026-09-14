using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;

namespace MinecraftHostApp;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // В приложении много "async void" обработчиков кликов - необработанное исключение в любом из них
    // раньше закрывало всё приложение целиком без единого сообщения ("выкидывает из приложения").
    // Теперь вместо краша показываем текст ошибки и приложение продолжает работать.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageWindow.Show(null, $"Произошла ошибка: {args.Exception.Message}\n\nПриложение продолжит работать.", "Ошибка");
            args.Handled = true;
        };
    }
}

