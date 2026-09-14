using System.IO;
using System.Text.Json;

namespace MinecraftHostApp.Models;

public class AppConfig
{
    public string FirebaseApiKey { get; set; } = "AIzaSyDBgmvGi2QhRIycENwWNEUTO_fKHZYKxto";
    public string FirebaseProjectId { get; set; } = "minecraft-rotating-host";
    // Отдельный ключ ImgBB именно для этого приложения (не переиспользует ключи других проектов).
    public string ImgBbApiKey { get; set; } = "ba9f62941fa4593accf0f1b509b5239a";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SharedFolder { get; set; } = "";
    public string PlayitAddress { get; set; } = "";
    // Сколько памяти давать серверу при хостинге (-Xmx), в мегабайтах. Выбирается перед стартом.
    public int ServerRamMb { get; set; } = 4096;
    // Путь к java.exe вручную - пусто значит "искать самому" (JAVA_HOME / PATH / стандартные папки).
    // Нужно только если автоопределение не нашло Java или нашло не ту версию.
    public string JavaPath { get; set; } = "";

    // OAuth-клиент Google Drive - один на всё приложение (десктоп-клиенты Google сам не считает
    // секретными, см. https://developers.google.com/identity/protocols/oauth2/native-app).
    public string GoogleClientId { get; set; } = "793250081177-naogi1vdomf0mfdk8g17aipoukvp5l07.apps.googleusercontent.com";
    public string GoogleClientSecret { get; set; } = "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";
    // Refresh token этого конкретного пользователя - выдаётся один раз при входе через Google,
    // личный для этого ПК/аккаунта, никогда никуда не отправляется кроме самого Google.
    public string GoogleRefreshToken { get; set; } = "";
    // ID корневой папки на Google Drive этого пользователя, где приложение хранит миры серверов.
    public string GoogleDriveFolderId { get; set; } = "";
    private string _serversDir = "";
    public string ServersDir
    {
        get => string.IsNullOrWhiteSpace(_serversDir)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "servers")
            : _serversDir;
        set => _serversDir = value;
    }

    // Куда ставить игровой клиент (Fabric/Forge профиль) для друзей, у которых ещё нет Minecraft
    // с нужным загрузчиком. По умолчанию - папка "clients" внутри .minecraft, как ожидают
    // лаунчеры с поддержкой нескольких клиентов (RuLauncher и подобные). Создаётся, если её нет.
    private string _clientInstallPath = "";
    public string ClientInstallPath
    {
        get
        {
            var path = string.IsNullOrWhiteSpace(_clientInstallPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "clients")
                : _clientInstallPath;
            Directory.CreateDirectory(path);
            return path;
        }
        set => _clientInstallPath = value;
    }

    // Пароли серверов, которые уже вводились на этом ПК - чтобы не спрашивать каждый раз.
    // Если пароль сервера сменили или тебя забанили - это всё равно проверяется заново на сервере (Firebase).
    public Dictionary<string, string> KnownServerPasswords { get; set; } = new();

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MinecraftHostApp");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static bool Exists() => File.Exists(ConfigPath);

    public static AppConfig Load()
    {
        if (!Exists()) return new AppConfig();
        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    // Для входа и просмотра/подключения к серверам ничего, кроме аккаунта, не нужно.
    // Папка Drive и адрес playit нужны только тому, кто сам собирается хостить -
    // это спрашивается отдельно, в момент первой попытки стать хостом на этом ПК.
    public bool IsHostReady() =>
        !string.IsNullOrWhiteSpace(SharedFolder) &&
        !string.IsNullOrWhiteSpace(PlayitAddress);
}
