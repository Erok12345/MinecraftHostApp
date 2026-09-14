using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MinecraftHostApp.Models;

namespace MinecraftHostApp.Services;

public class ServerManager
{
    private readonly AppConfig _config;
    private readonly FirebaseClient _fb;
    private readonly GoogleDriveClient _drive;
    private readonly HttpClient _http = new();

    public ServerManager(AppConfig config, FirebaseClient fb)
    {
        _config = config;
        _fb = fb;
        _drive = new GoogleDriveClient(config);
    }

    // Папка этого сервера внутри общей папки приложения на Google Drive пользователя.
    private async Task<string> EnsureServerDriveFolderAsync(string serverId)
    {
        var root = await _drive.EnsureRootFolderAsync();
        return await _drive.FindOrCreateFolderAsync(serverId, root);
    }

    public bool IsDriveConnected => _drive.IsConnected;

    // Даёт другу доступ к папке этого сервера на Google Drive прямо по email - без ручного шаринга
    // через сайт Drive и без нужды присылать ссылку.
    public async Task ShareServerFolderAsync(string serverId, string email)
    {
        var folderId = await EnsureServerDriveFolderAsync(serverId);
        await _drive.ShareWithEmailAsync(folderId, email);
    }

    // Кладёт свой мир (скачанную карту, старый сингл и т.п.) как новый "последний" архив мира этого
    // сервера - при следующем "Стать хостом" ClaimAndUnpackAsync подхватит его как самый свежий,
    // так что отдельная логика распаковки не нужна. sourcePath - либо папка с level.dat, либо .zip.
    public async Task UploadOwnWorldAsync(string serverId, string sourcePath)
    {
        var bytes = Directory.Exists(sourcePath)
            ? ZipDirToBytes(sourcePath, entryRoot: "world")
            : EnsureWorldRootInZip(await File.ReadAllBytesAsync(sourcePath));

        var zipName = $"world_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
        if (_drive.IsConnected)
        {
            var folderId = await EnsureServerDriveFolderAsync(serverId);
            await _drive.UploadFileAsync(folderId, zipName, bytes);
        }
        else
        {
            var sharedDir = SharedDir(serverId);
            Directory.CreateDirectory(sharedDir);
            await File.WriteAllBytesAsync(Path.Combine(sharedDir, zipName), bytes);
        }
    }

    // Если в зипе нет корневой папки "world/" (человек зазиповал сам level.dat и соседние файлы/папки
    // напрямую) - оборачиваем на лету, чтобы структура совпадала с тем, что ожидает ClaimAndUnpackAsync.
    private static byte[] EnsureWorldRootInZip(byte[] raw)
    {
        using var msIn = new MemoryStream(raw);
        using var zipIn = new ZipArchive(msIn, ZipArchiveMode.Read);
        if (zipIn.Entries.Any(e => e.FullName.StartsWith("world/"))) return raw;

        using var msOut = new MemoryStream();
        using (var zipOut = new ZipArchive(msOut, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in zipIn.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                using var src = entry.Open();
                using var dst = zipOut.CreateEntry($"world/{entry.FullName}").Open();
                src.CopyTo(dst);
            }
        }
        return msOut.ToArray();
    }

    private static byte[] ZipDirToBytes(string sourceDir, string? entryRoot = null, Func<string, bool>? skip = null)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                if (skip != null && skip(relative)) continue;
                var entryName = entryRoot != null ? $"{entryRoot}/{relative}" : relative;
                archive.CreateEntryFromFile(file, entryName);
            }
        }
        return ms.ToArray();
    }

    private static void ExtractZipBytes(byte[] bytes, string destDir)
    {
        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        archive.ExtractToDirectory(destDir, overwriteFiles: true);
    }

    // Ручной путь из настроек (если задан и существует) важнее автопоиска.
    private string JavaExe =>
        !string.IsNullOrWhiteSpace(_config.JavaPath) && File.Exists(_config.JavaPath)
            ? _config.JavaPath
            : JavaLocator.Find() ?? "java";

    // Само приложение туннель не поднимает - его создаёт отдельно запущенный агент playit.gg на этом ПК.
    // Проверяем по имени процесса, чтобы подсказать хосту, если он забыл его запустить.
    public static bool IsPlayitRunning() =>
        System.Diagnostics.Process.GetProcesses().Any(p =>
        {
            try { return p.ProcessName.Contains("playit", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });

    // Порт, на который реально настроен сервер (из server.properties) - чтобы хост знал, на какой порт
    // должен указывать туннель playit.gg. До первого запуска сервера файла ещё нет - тогда это 25565 (дефолт Minecraft).
    public int GetServerPort(string serverId)
    {
        var path = Path.Combine(ServerDir(serverId), "server.properties");
        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.StartsWith("server-port=") && int.TryParse(line["server-port=".Length..].Trim(), out var port))
                    return port;
            }
        }
        return 25565;
    }

    // Пишет набор свойств (ключ -> значение server.properties) перед запуском - вызывающий код сам
    // решает, что именно передать (см. ServerLobbyControl.SaveServerSettings_Click). online-mode по
    // умолчанию false (пиратка), т.к. большинство друзей заходят не с лицензионных аккаунтов, и с
    // online-mode=true Mojang просто не пустит их с ошибкой "недействительная сессия".
    public void ApplyServerSettings(string serverId, Dictionary<string, string> properties)
    {
        var path = Path.Combine(ServerDir(serverId), "server.properties");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        foreach (var (key, value) in properties) SetProperty(lines, key, value);
        File.WriteAllLines(path, lines);
    }

    private static void SetProperty(List<string> lines, string key, string value)
    {
        var prefix = key + "=";
        var idx = lines.FindIndex(l => l.StartsWith(prefix));
        if (idx >= 0) lines[idx] = prefix + value;
        else lines.Add(prefix + value);
    }

    // Ник в Minecraft - это не Firebase UID, а игровое имя (для пиратки его можно ставить любым в лаунчере).
    // По умолчанию сервер offline-mode, поэтому UUID для ops.json считаем по формуле оффлайн-игрока
    // (та же, что использует сам ванильный сервер): MD5("OfflinePlayer:" + имя) с версией 3.
    public static string OfflineUuid(string username)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash).ToString();
    }

    // Возвращает true, если сервер сейчас запущен с этого ПК и команда отправлена сразу (без рестарта).
    public bool AddOperator(string serverId, string username)
    {
        var path = Path.Combine(ServerDir(serverId), "ops.json");
        var ops = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? new JsonArray() : new JsonArray();
        if (!ops.Any(o => string.Equals(o?["name"]?.ToString(), username, StringComparison.OrdinalIgnoreCase)))
        {
            ops.Add(new JsonObject
            {
                ["uuid"] = OfflineUuid(username),
                ["name"] = username,
                ["level"] = 4,
                ["bypassesPlayerLimit"] = false,
            });
            Directory.CreateDirectory(ServerDir(serverId));
            File.WriteAllText(path, ops.ToJsonString());
        }
        return SendCommand(serverId, $"op {username}");
    }

    public bool RemoveOperator(string serverId, string username)
    {
        var path = Path.Combine(ServerDir(serverId), "ops.json");
        if (File.Exists(path))
        {
            var ops = JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? new JsonArray();
            var remaining = new JsonArray(ops.Where(o => !string.Equals(o?["name"]?.ToString(), username, StringComparison.OrdinalIgnoreCase))
                .Select(o => o!.DeepClone()).ToArray());
            File.WriteAllText(path, remaining.ToJsonString());
        }
        return SendCommand(serverId, $"deop {username}");
    }

    // Та же логика, что и с ops.json - галка "Белый список" в настройках только включает
    // white-list=true в server.properties, а сами разрешённые ники нужно прописывать отдельно сюда.
    public bool AddWhitelist(string serverId, string username)
    {
        var path = Path.Combine(ServerDir(serverId), "whitelist.json");
        var list = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? new JsonArray() : new JsonArray();
        if (!list.Any(o => string.Equals(o?["name"]?.ToString(), username, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(new JsonObject
            {
                ["uuid"] = OfflineUuid(username),
                ["name"] = username,
            });
            Directory.CreateDirectory(ServerDir(serverId));
            File.WriteAllText(path, list.ToJsonString());
        }
        return SendCommand(serverId, $"whitelist add {username}");
    }

    public bool RemoveWhitelist(string serverId, string username)
    {
        var path = Path.Combine(ServerDir(serverId), "whitelist.json");
        if (File.Exists(path))
        {
            var list = JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? new JsonArray();
            var remaining = new JsonArray(list.Where(o => !string.Equals(o?["name"]?.ToString(), username, StringComparison.OrdinalIgnoreCase))
                .Select(o => o!.DeepClone()).ToArray());
            File.WriteAllText(path, remaining.ToJsonString());
        }
        return SendCommand(serverId, $"whitelist remove {username}");
    }

    public async Task<List<string>> GetMojangReleaseVersionsAsync()
    {
        var manifest = await _http.GetFromJsonAsync<JsonObject>("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        return manifest!["versions"]!.AsArray()
            .Where(v => v!["type"]!.ToString() == "release")
            .Select(v => v!["id"]!.ToString())
            .ToList();
    }

    public string ServerDir(string id) => Path.Combine(_config.ServersDir, id);
    public string SharedDir(string id) => Path.Combine(_config.SharedFolder, id);

    public async Task<(bool ok, string message)> ClaimAndUnpackAsync(string serverId, string password, string loader, string mcVersion, IProgress<string>? log = null)
    {
        var hash = FirebaseClient.Sha256Hex(password);
        await _fb.SetDocAsync($"sessions/{serverId}", new Dictionary<string, object>
        {
            ["hostUid"] = _fb.Uid!,
            ["status"] = "hosting",
            ["startedAt"] = DateTime.UtcNow.ToString("o"),
        });
        await _fb.SetDocAsync($"sessions/{serverId}/access/{hash}", new Dictionary<string, object>
        {
            ["address"] = _config.PlayitAddress,
        });

        var serverDir = ServerDir(serverId);
        var worldDir = Path.Combine(serverDir, "world");
        var useDrive = _drive.IsConnected;
        var messages = new List<string>();

        // Если на этом ПК ещё нет файлов сервера (jar/моды) - его тут никто не хостил,
        // значит их надо взять из общего хранилища (Google Drive, если подключён, иначе старая
        // общая папка) или, если и там нет, - собрать заново.
        var launchCfgPath = Path.Combine(serverDir, "launch.json");
        if (!File.Exists(launchCfgPath))
        {
            byte[]? filesBytes = null;
            if (useDrive)
            {
                var folderId = await EnsureServerDriveFolderAsync(serverId);
                var match = (await _drive.ListFilesAsync(folderId, "serverfiles.zip")).FirstOrDefault(f => f.Name == "serverfiles.zip");
                if (match.Id != null)
                {
                    log?.Report("Файлов сервера тут ещё нет - скачиваю с Google Drive...");
                    filesBytes = await _drive.DownloadFileAsync(match.Id);
                }
            }
            else
            {
                var sharedDir = SharedDir(serverId);
                Directory.CreateDirectory(sharedDir);
                var filesZip = new FileInfo(Path.Combine(sharedDir, "serverfiles.zip"));
                if (filesZip.Exists)
                {
                    log?.Report("Файлов сервера тут ещё нет - скачиваю из общей папки...");
                    filesBytes = await File.ReadAllBytesAsync(filesZip.FullName);
                }
            }

            if (filesBytes != null)
            {
                ExtractZipBytes(filesBytes, serverDir);
                messages.Add("Файлы сервера (jar/моды) скачаны.");
            }
            else
            {
                log?.Report("Файлов сервера нигде нет - собираю заново...");
                var err = loader switch
                {
                    "fabric" => await CreateFabricServerAsync(serverId, mcVersion, log ?? new Progress<string>()),
                    _ => await CreateVanillaServerAsync(serverId, mcVersion, log ?? new Progress<string>()),
                };
                if (!string.IsNullOrEmpty(err)) return (false, err);
                await SyncServerFilesToCloudAsync(serverId);
                messages.Add("Файлы сервера собраны заново и загружены для следующих хостов.");
            }
        }

        byte[]? worldBytes = null;
        var worldSourceName = "";
        if (useDrive)
        {
            var folderId = await EnsureServerDriveFolderAsync(serverId);
            var latest = (await _drive.ListFilesAsync(folderId, "world_")).OrderByDescending(f => f.Modified).FirstOrDefault();
            if (latest.Id != null) { worldBytes = await _drive.DownloadFileAsync(latest.Id); worldSourceName = latest.Name; }
        }
        else
        {
            var sharedDir = SharedDir(serverId);
            Directory.CreateDirectory(sharedDir);
            var latestZip = new DirectoryInfo(sharedDir).GetFiles("world_*.zip").OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (latestZip != null) { worldBytes = await File.ReadAllBytesAsync(latestZip.FullName); worldSourceName = latestZip.Name; }
        }

        if (worldBytes != null)
        {
            if (Directory.Exists(worldDir))
                Directory.Move(worldDir, Path.Combine(serverDir, "world_local_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")));
            ExtractZipBytes(worldBytes, serverDir);
            messages.Add($"Мир восстановлен из {worldSourceName}.");
        }
        else
        {
            messages.Add("Архива мира ещё нет - сервер сгенерирует новый.");
        }
        return (true, string.Join("\n", messages));
    }

    public async Task SyncServerFilesToCloudAsync(string serverId)
    {
        var serverDir = ServerDir(serverId);
        var bytes = ZipDirToBytes(serverDir, skip: relative => relative.StartsWith("world") || relative.EndsWith(".zip"));

        if (_drive.IsConnected)
        {
            var folderId = await EnsureServerDriveFolderAsync(serverId);
            var existingId = (await _drive.ListFilesAsync(folderId, "serverfiles.zip")).FirstOrDefault(f => f.Name == "serverfiles.zip").Id;
            await _drive.UploadFileAsync(folderId, "serverfiles.zip", bytes, existingId);
            return;
        }

        var sharedDir = SharedDir(serverId);
        Directory.CreateDirectory(sharedDir);
        var zipPath = Path.Combine(sharedDir, "serverfiles.zip");
        var tmpZip = zipPath + ".tmp";
        await File.WriteAllBytesAsync(tmpZip, bytes);
        File.Move(tmpZip, zipPath, overwrite: true);
    }

    public async Task<(bool ok, string message)> PackAndReleaseAsync(string serverId, string password)
    {
        // Если сервер ещё работает - сначала аккуратно останавливаем его командой "stop" и ждём
        // выхода процесса, иначе можно заархивировать мир прямо во время записи чанков на диск
        // (риск повреждения мира).
        if (_runningServers.TryGetValue(serverId, out var proc) && !proc.HasExited)
        {
            SendCommand(serverId, "stop");
            try { await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token); }
            catch (OperationCanceledException) { /* не остановился вовремя - архивируем как есть */ }
        }

        var worldDir = Path.Combine(ServerDir(serverId), "world");
        if (!Directory.Exists(worldDir))
            return (false, "Папка мира не найдена - сервер точно запускался?");

        // Compress-Archive кладёт папку world/ внутрь архива (как делали PS-скрипты) - повторяем
        // ту же структуру: архив содержит один корневой каталог "world".
        var bytes = ZipDirToBytes(worldDir, entryRoot: "world");
        var zipName = $"world_{DateTime.Now:yyyyMMdd_HHmmss}.zip";

        if (_drive.IsConnected)
        {
            var folderId = await EnsureServerDriveFolderAsync(serverId);
            await _drive.UploadFileAsync(folderId, zipName, bytes);
            var allWorldZips = await _drive.ListFilesAsync(folderId, "world_");
            foreach (var old in allWorldZips.OrderByDescending(f => f.Modified).Skip(5))
                await _drive.DeleteFileAsync(old.Id);
        }
        else
        {
            var sharedDir = SharedDir(serverId);
            Directory.CreateDirectory(sharedDir);
            var zipPath = Path.Combine(sharedDir, zipName);
            var tmpZip = zipPath + ".tmp";
            await File.WriteAllBytesAsync(tmpZip, bytes);
            File.Move(tmpZip, zipPath);

            foreach (var old in new DirectoryInfo(sharedDir).GetFiles("world_*.zip")
                         .OrderByDescending(f => f.LastWriteTime).Skip(5))
                old.Delete();
        }

        var hash = FirebaseClient.Sha256Hex(password);
        await _fb.SetDocAsync($"sessions/{serverId}", new Dictionary<string, object>
        {
            ["hostUid"] = "",
            ["status"] = "idle",
        });
        await _fb.SetDocAsync($"sessions/{serverId}/access/{hash}", new Dictionary<string, object>
        {
            ["address"] = "",
        });
        return (true, _drive.IsConnected ? "Мир сохранён на Google Drive, слот освобождён." : "Мир сохранён в общей папке, слот освобождён.");
    }

    // archive=true - старый мир переносится в подпапку "archived_worlds" (и локальная копия,
    // если она есть на этом ПК), чтобы можно было восстановить вручную. archive=false - просто удаляется,
    // если он никому не нужен и незачем занимать место на Drive.
    public async Task<string> ResetWorldAsync(string serverId, bool archive)
    {
        var worldDir = Path.Combine(ServerDir(serverId), "world");
        var affected = 0;

        if (Directory.Exists(worldDir))
        {
            if (archive)
            {
                var bytes = ZipDirToBytes(worldDir, entryRoot: "world");
                var name = $"world_local_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
                if (_drive.IsConnected)
                {
                    var folderId = await EnsureServerDriveFolderAsync(serverId);
                    var archiveFolderId = await _drive.FindOrCreateFolderAsync("archived_worlds", folderId);
                    await _drive.UploadFileAsync(archiveFolderId, name, bytes);
                }
                else
                {
                    var archiveDir = Path.Combine(SharedDir(serverId), "archived_worlds");
                    Directory.CreateDirectory(archiveDir);
                    await File.WriteAllBytesAsync(Path.Combine(archiveDir, name), bytes);
                }
            }
            Directory.Delete(worldDir, recursive: true);
            affected++;
        }

        if (_drive.IsConnected)
        {
            var folderId = await EnsureServerDriveFolderAsync(serverId);
            var worldZips = await _drive.ListFilesAsync(folderId, "world_");
            if (archive && worldZips.Count > 0)
            {
                var archiveFolderId = await _drive.FindOrCreateFolderAsync("archived_worlds", folderId);
                foreach (var zip in worldZips)
                    await _drive.MoveFileAsync(zip.Id, archiveFolderId, folderId);
            }
            else
            {
                foreach (var zip in worldZips) await _drive.DeleteFileAsync(zip.Id);
            }
            affected += worldZips.Count;
        }
        else
        {
            var sharedDir = SharedDir(serverId);
            Directory.CreateDirectory(sharedDir);
            var worldZips = new DirectoryInfo(sharedDir).GetFiles("world_*.zip");
            if (archive && worldZips.Length > 0)
            {
                var archiveDir = Path.Combine(sharedDir, "archived_worlds");
                Directory.CreateDirectory(archiveDir);
                foreach (var zip in worldZips)
                    File.Move(zip.FullName, Path.Combine(archiveDir, zip.Name), overwrite: true);
            }
            else
            {
                foreach (var zip in worldZips) zip.Delete();
            }
            affected += worldZips.Length;
        }

        if (affected == 0)
            return "Мир и так пустой - сбрасывать нечего, при старте сервер сам сгенерирует новый.";

        return archive
            ? "Старый мир перенесён в папку 'archived_worlds' и больше не используется. При следующем запуске сервер сгенерирует новый мир."
            : "Старый мир удалён без архивации. При следующем запуске сервер сгенерирует новый мир.";
    }

    // Процессы уже запущенных серверов (на этом ПК, в рамках текущего запуска приложения) -
    // нужно, чтобы слать команды (op/deop/stop) прямо в консоль сервера без перезапуска.
    private readonly Dictionary<string, System.Diagnostics.Process> _runningServers = new();

    // Строчка лога сервера - для встроенной консоли в приложении (как на Aternos), без отдельного окна.
    public event Action<string, string>? ServerOutput; // serverId, line

    public bool IsServerRunningHere(string serverId) => _runningServers.TryGetValue(serverId, out var p) && !p.HasExited;

    public void StartServerProcess(string serverId, int ramMb = 4096)
    {
        var serverDir = ServerDir(serverId);
        var launchCfgPath = Path.Combine(serverDir, "launch.json");
        var cfg = JsonNode.Parse(File.ReadAllText(launchCfgPath))!;
        var launchType = cfg["launchType"]!.ToString();
        var launchFile = cfg["launchFile"]!.ToString();

        if (launchType == "script")
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/k \"{Path.Combine(serverDir, launchFile)}\" nogui")
            {
                WorkingDirectory = serverDir,
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
            return;
        }

        var xms = Math.Max(512, ramMb / 2);
        // Запускаем java.exe напрямую (не через cmd.exe /k - заодно убирает старый баг с кавычками
        // при пути с пробелами вроде "C:\Program Files\..."). Полностью перенаправляем ввод/вывод и
        // прячем окно консоли - все логи и команды теперь идут через встроенную консоль в приложении
        // (см. ServerOutput/SendCommand), отдельное чёрное окно Windows больше не нужно.
        var directPsi = new System.Diagnostics.ProcessStartInfo(JavaExe, $"-Xmx{ramMb}M -Xms{xms}M -jar \"{launchFile}\" nogui")
        {
            WorkingDirectory = serverDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var proc = new System.Diagnostics.Process { StartInfo = directPsi, EnableRaisingEvents = true };
        // Эти обработчики выполняются в фоновом потоке .NET (не в UI) - необработанное исключение
        // здесь убивает всё приложение целиком, а не просто эту функцию, поэтому каждый - в try/catch.
        proc.OutputDataReceived += (_, args) => SafeInvoke(() => { if (args.Data != null) ServerOutput?.Invoke(serverId, args.Data); });
        proc.ErrorDataReceived += (_, args) => SafeInvoke(() => { if (args.Data != null) ServerOutput?.Invoke(serverId, args.Data); });
        proc.Exited += (_, _) => SafeInvoke(() =>
        {
            _runningServers.Remove(serverId);
            ServerOutput?.Invoke(serverId, "[сервер остановлен]");
        });
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _runningServers[serverId] = proc;
    }

    private static void SafeInvoke(Action action)
    {
        try { action(); } catch { /* сбой в подписчике (например UI-обработчике) не должен ронять процесс сервера */ }
    }

    // true - команда реально отправлена в запущенный на этом ПК сервер (применится сразу).
    // false - сервер сейчас не запущен отсюда, или отправить не удалось (например процесс только что сам завершился) -
    // изменение (например ops.json) подхватится только при следующем старте.
    public bool SendCommand(string serverId, string command)
    {
        if (!_runningServers.TryGetValue(serverId, out var proc) || proc.HasExited) return false;
        try
        {
            proc.StandardInput.WriteLine(command);
            proc.StandardInput.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> DownloadClientAsync(string serverId, string loader, string mcVersion, string baseClientPath, IProgress<string> log)
    {
        // Никакой официальный лаунчер тут не ставится и не нужен - многие играют через
        // сторонние/оффлайн-лаунчеры (RuLauncher, TLauncher и т.п.), у которых часто есть папка
        // "clients" с несколькими профилями - кладём файлы каждого сервера в свою подпапку там.
        var clientPath = Path.Combine(baseClientPath, serverId);
        Directory.CreateDirectory(clientPath);

        if (loader == "vanilla")
        {
            return $"Для vanilla {mcVersion} ничего скачивать не нужно - выбери эту версию " +
                   "в любом твоём лаунчере (официальном, TLauncher и т.п.), он сам её подтянет.";
        }

        if (loader == "fabric")
        {
            Directory.CreateDirectory(clientPath);
            log.Report("Узнаю последние версии Fabric installer/loader...");
            var installers = await _http.GetFromJsonAsync<JsonArray>("https://meta.fabricmc.net/v2/versions/installer");
            var installerVer = installers!.First(x => x!["stable"]!.GetValue<bool>())!["version"]!.ToString();
            var loaders = await _http.GetFromJsonAsync<JsonArray>("https://meta.fabricmc.net/v2/versions/loader");
            var loaderVer = loaders!.First(x => x!["stable"]!.GetValue<bool>())!["version"]!.ToString();

            var installerPath = Path.Combine(Path.GetTempPath(), "fabric-installer-client.jar");
            log.Report("Скачиваю Fabric installer...");
            await DownloadFileAsync($"https://maven.fabricmc.net/net/fabricmc/fabric-installer/{installerVer}/fabric-installer-{installerVer}.jar", installerPath);

            log.Report("Собираю файлы клиента в папку...");
            var psi = new System.Diagnostics.ProcessStartInfo(JavaExe,
                $"-jar \"{installerPath}\" client -mcversion {mcVersion} -loader {loaderVer} -dir \"{clientPath}\" -noprofile")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0) return "Fabric client installer завершился с ошибкой.";

            var clientModsDir = Path.Combine(clientPath, "mods");
            Directory.CreateDirectory(clientModsDir);
            log.Report("Скачиваю установленные моды сервера на клиент...");
            await InstallModsIntoClientAsync(serverId, loader, mcVersion, clientModsDir);

            log.Report("Скачиваю ресурспаки сервера на клиент...");
            await InstallResourcePacksIntoClientAsync(serverId, Path.Combine(clientPath, "resourcepacks"));

            return $"Готово! Файлы лежат в {clientPath} (моды и ресурспаки сервера скачаны туда же).\n" +
                   "Укажи эту папку как игровую директорию в своём лаунчере (официальном, TLauncher, " +
                   "MultiMC/Prism и т.п. - там где можно выбрать свою папку под профиль) и выбери профиль Fabric.";
        }

        return "Forge-клиент пока не реализован в приложении.";
    }

    public async Task<string> CreateVanillaServerAsync(string serverId, string mcVersion, IProgress<string> log)
    {
        var dir = ServerDir(serverId);
        Directory.CreateDirectory(dir);
        log.Report("Ищу ссылку на server.jar в манифесте Mojang...");
        var manifest = await _http.GetFromJsonAsync<JsonObject>("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        var entry = manifest!["versions"]!.AsArray().FirstOrDefault(v => v!["id"]!.ToString() == mcVersion);
        if (entry == null) return $"Версия {mcVersion} не найдена в манифесте Mojang.";
        var verMeta = await _http.GetFromJsonAsync<JsonObject>(entry["url"]!.ToString());
        var jarUrl = verMeta!["downloads"]!["server"]!["url"]!.ToString();

        log.Report("Скачиваю server.jar...");
        await DownloadFileAsync(jarUrl, Path.Combine(dir, "server.jar"));
        File.WriteAllText(Path.Combine(dir, "launch.json"), "{\"loader\":\"vanilla\",\"launchType\":\"jar\",\"launchFile\":\"server.jar\"}");
        WriteEula(dir);
        return "";
    }

    public async Task<string> CreateFabricServerAsync(string serverId, string mcVersion, IProgress<string> log)
    {
        var dir = ServerDir(serverId);
        Directory.CreateDirectory(dir);

        log.Report("Узнаю последние версии Fabric installer/loader...");
        var installers = await _http.GetFromJsonAsync<JsonArray>("https://meta.fabricmc.net/v2/versions/installer");
        var installerVer = installers!.First(x => x!["stable"]!.GetValue<bool>())!["version"]!.ToString();
        var loaders = await _http.GetFromJsonAsync<JsonArray>("https://meta.fabricmc.net/v2/versions/loader");
        var loaderVer = loaders!.First(x => x!["stable"]!.GetValue<bool>())!["version"]!.ToString();

        var installerPath = Path.Combine(dir, "fabric-installer.jar");
        log.Report("Скачиваю Fabric installer...");
        await DownloadFileAsync($"https://maven.fabricmc.net/net/fabricmc/fabric-installer/{installerVer}/fabric-installer-{installerVer}.jar", installerPath);

        log.Report("Запускаю установщик Fabric-сервера...");
        var psi = new System.Diagnostics.ProcessStartInfo(JavaExe,
            $"-jar \"{installerPath}\" server -mcversion {mcVersion} -loader {loaderVer} -downloadMinecraft")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) return "Fabric installer завершился с ошибкой.";

        Directory.CreateDirectory(Path.Combine(dir, "mods"));
        File.WriteAllText(Path.Combine(dir, "launch.json"), "{\"loader\":\"fabric\",\"launchType\":\"jar\",\"launchFile\":\"fabric-server-launch.jar\"}");
        WriteEula(dir);
        return "";
    }

    private static void WriteEula(string dir) =>
        File.WriteAllText(Path.Combine(dir, "eula.txt"),
            "#By changing the setting below to TRUE you are indicating your agreement to our EULA (https://aka.ms/MinecraftEULA).\neula=true\n");

    private async Task DownloadFileAsync(string url, string dest)
    {
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(dest);
        await resp.Content.CopyToAsync(fs);
    }

    // clientSide/serverSide - поля мода из Modrinth ("required"/"optional"/"unsupported").
    // Если serverSide == "unsupported" (мод только для клиента, например мини-карта или шейдеры) -
    // файл на сервер НЕ кладём (иначе dedicated-сервер может не запуститься), но запись всё равно
    // сохраняем, чтобы мод попал в папку клиента при скачивании (см. InstallModsIntoClientAsync).
    // specificVersion - если задан явно (выбор конкретной версии в карточке мода), качаем именно его,
    // иначе берём самый свежий под этот загрузчик+версию Minecraft.
    public async Task<string> InstallModAsync(string serverId, string projectId, string modTitle, string loader, string mcVersion,
        string clientSide = "unknown", string serverSide = "unknown", ModVersionInfo? specificVersion = null)
    {
        var modrinth = new ModrinthClient();
        var file = specificVersion ?? await modrinth.GetLatestVersionAsync(projectId, loader, mcVersion);
        if (file == null) return $"Для '{modTitle}' не нашлось файла под {loader} {mcVersion}.";

        var modsDir = Path.Combine(ServerDir(serverId), "mods");
        Directory.CreateDirectory(modsDir);

        // Если мод (или другая версия того же мода) уже стоял - сначала убираем старый jar,
        // иначе при смене версии на сервере останутся оба файла одновременно.
        var doc = await _fb.GetDocAsync($"servers/{serverId}");
        var oldEntry = doc.GetArray("installedMods").FirstOrDefault(s => s.StartsWith(projectId + "|"));
        if (oldEntry != null)
        {
            var oldParts = oldEntry.Split('|');
            var oldFileName = oldParts.Length > 4 ? oldParts[4] : "";
            if (!string.IsNullOrEmpty(oldFileName))
            {
                var oldPath = Path.Combine(modsDir, oldFileName);
                if (File.Exists(oldPath)) File.Delete(oldPath);
            }
        }

        if (serverSide != "unsupported")
        {
            var bytes = await modrinth.DownloadBytesAsync(file.FileUrl);
            await File.WriteAllBytesAsync(Path.Combine(modsDir, file.FileName), bytes);
            await SyncServerFilesToCloudAsync(serverId);
        }

        var current = doc.GetArray("installedMods").Where(s => !s.StartsWith(projectId + "|")).ToList();
        current.Add($"{projectId}|{modTitle}|{clientSide}|{serverSide}|{file.FileName}|{file.FileUrl}");
        await _fb.SetDocAsync($"servers/{serverId}", new Dictionary<string, object> { ["installedMods"] = current });

        return serverSide == "unsupported"
            ? $"{modTitle} - клиентский мод, на сервер не ставится, но появится у друзей при скачивании клиента."
            : "";
    }

    public async Task<string> RemoveModAsync(string serverId, string projectId)
    {
        var doc = await _fb.GetDocAsync($"servers/{serverId}");
        var entry = doc.GetArray("installedMods").FirstOrDefault(s => s.StartsWith(projectId + "|"));
        var current = doc.GetArray("installedMods").Where(s => !s.StartsWith(projectId + "|")).ToList();
        await _fb.SetDocAsync($"servers/{serverId}", new Dictionary<string, object> { ["installedMods"] = current });

        if (entry != null)
        {
            var parts = entry.Split('|');
            var fileName = parts.Length > 4 ? parts[4] : "";
            if (!string.IsNullOrEmpty(fileName))
            {
                var path = Path.Combine(ServerDir(serverId), "mods", fileName);
                if (File.Exists(path)) File.Delete(path);
                await SyncServerFilesToCloudAsync(serverId);
            }
        }

        return entry ?? "";
    }

    public async Task InstallModsIntoClientAsync(string serverId, string loader, string mcVersion, string clientModsDir)
    {
        var doc = await _fb.GetDocAsync($"servers/{serverId}");
        var mods = doc.GetArray("installedMods");
        if (mods.Count == 0) return;

        var modrinth = new ModrinthClient();
        Directory.CreateDirectory(clientModsDir);
        foreach (var entry in mods)
        {
            var parts = entry.Split('|');
            var projectId = parts[0];
            // parts[2] - clientSide: если мод помечен как "unsupported" на клиенте (чисто серверный,
            // например оптимизатор) - класть его в клиентскую папку нельзя, лаунчер может не запуститься.
            var clientSide = parts.Length > 2 ? parts[2] : "unknown";
            if (clientSide == "unsupported") continue;

            // parts[5] - точная ссылка на файл, который реально стоит на сервере (если она сохранена) -
            // берём именно её, чтобы у друга в клиенте была ТА ЖЕ версия мода, а не случайно другая "последняя".
            var fileUrl = parts.Length > 5 ? parts[5] : "";
            string fileName;
            if (!string.IsNullOrEmpty(fileUrl))
            {
                fileName = parts.Length > 4 ? parts[4] : Path.GetFileName(new Uri(fileUrl).LocalPath);
            }
            else
            {
                var file = await modrinth.GetLatestVersionAsync(projectId, loader, mcVersion);
                if (file == null) continue;
                fileUrl = file.FileUrl;
                fileName = file.FileName;
            }
            var bytes = await modrinth.DownloadBytesAsync(fileUrl);
            await File.WriteAllBytesAsync(Path.Combine(clientModsDir, fileName), bytes);
        }
    }

    // Ресурспаки нужны только клиенту - на сервер файл не кладём вообще, только запоминаем,
    // чтобы скачать другу при "Скачать клиент" (см. InstallResourcePacksIntoClientAsync).
    public async Task InstallResourcePackAsync(string serverId, string projectId, string title, string fileName, string fileUrl)
    {
        var doc = await _fb.GetDocAsync($"servers/{serverId}");
        var current = doc.GetArray("installedResourcePacks").Where(s => !s.StartsWith(projectId + "|")).ToList();
        current.Add($"{projectId}|{title}|{fileName}|{fileUrl}");
        await _fb.SetDocAsync($"servers/{serverId}", new Dictionary<string, object> { ["installedResourcePacks"] = current });
    }

    public async Task RemoveResourcePackAsync(string serverId, string projectId)
    {
        var doc = await _fb.GetDocAsync($"servers/{serverId}");
        var current = doc.GetArray("installedResourcePacks").Where(s => !s.StartsWith(projectId + "|")).ToList();
        await _fb.SetDocAsync($"servers/{serverId}", new Dictionary<string, object> { ["installedResourcePacks"] = current });
    }

    public async Task InstallResourcePacksIntoClientAsync(string serverId, string clientResourcePacksDir)
    {
        var doc = await _fb.GetDocAsync($"servers/{serverId}");
        var packs = doc.GetArray("installedResourcePacks");
        if (packs.Count == 0) return;

        var modrinth = new ModrinthClient();
        Directory.CreateDirectory(clientResourcePacksDir);
        foreach (var entry in packs)
        {
            var parts = entry.Split('|');
            if (parts.Length < 4) continue;
            var bytes = await modrinth.DownloadBytesAsync(parts[3]);
            await File.WriteAllBytesAsync(Path.Combine(clientResourcePacksDir, parts[2]), bytes);
        }
    }

    // Датапаки нужны только серверу - кладём zip-файл прямо в world/datapacks, Minecraft подхватывает
    // такие файлы без распаковки. Так как это часть world/, датапак сам едет вместе с миром при
    // сохранении/скачивании (PackAndReleaseAsync/ClaimAndUnpackAsync) - отдельно синхронизировать не нужно.
    public async Task InstallDatapackAsync(string serverId, string projectId, string title, string mcVersion, ModVersionInfo? specificVersion = null)
    {
        var modrinth = new ModrinthClient();
        var file = specificVersion ?? await modrinth.GetLatestContentVersionAsync(projectId, mcVersion);
        if (file == null) throw new Exception($"Для '{title}' не нашлось файла под версию {mcVersion}.");

        var datapacksDir = Path.Combine(ServerDir(serverId), "world", "datapacks");
        Directory.CreateDirectory(datapacksDir);

        var doc = await _fb.GetDocAsync($"servers/{serverId}");
        var oldEntry = doc.GetArray("installedDatapacks").FirstOrDefault(s => s.StartsWith(projectId + "|"));
        if (oldEntry != null)
        {
            var oldParts = oldEntry.Split('|');
            var oldFileName = oldParts.Length > 2 ? oldParts[2] : "";
            if (!string.IsNullOrEmpty(oldFileName) && oldFileName != file.FileName)
            {
                var oldPath = Path.Combine(datapacksDir, oldFileName);
                if (File.Exists(oldPath)) File.Delete(oldPath);
            }
        }

        var bytes = await modrinth.DownloadBytesAsync(file.FileUrl);
        await File.WriteAllBytesAsync(Path.Combine(datapacksDir, file.FileName), bytes);

        var current = doc.GetArray("installedDatapacks").Where(s => !s.StartsWith(projectId + "|")).ToList();
        current.Add($"{projectId}|{title}|{file.FileName}");
        await _fb.SetDocAsync($"servers/{serverId}", new Dictionary<string, object> { ["installedDatapacks"] = current });
    }

    public async Task RemoveDatapackAsync(string serverId, string projectId)
    {
        var doc = await _fb.GetDocAsync($"servers/{serverId}");
        var entry = doc.GetArray("installedDatapacks").FirstOrDefault(s => s.StartsWith(projectId + "|"));
        var current = doc.GetArray("installedDatapacks").Where(s => !s.StartsWith(projectId + "|")).ToList();
        await _fb.SetDocAsync($"servers/{serverId}", new Dictionary<string, object> { ["installedDatapacks"] = current });

        if (entry != null)
        {
            var parts = entry.Split('|');
            if (parts.Length > 2)
            {
                var path = Path.Combine(ServerDir(serverId), "world", "datapacks", parts[2]);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    public async Task RegisterServerAsync(string serverId, string loader, string mcVersion, string password, bool hidden)
    {
        var hash = FirebaseClient.Sha256Hex(password);
        await _fb.SetDocAsync($"servers/{serverId}", new Dictionary<string, object>
        {
            ["name"] = serverId,
            ["loader"] = loader,
            ["mcVersion"] = mcVersion,
            ["createdBy"] = _fb.Uid!,
            ["joinPasswordHash"] = hash,
            ["allowedHosts"] = new List<string> { _fb.Uid! },
            ["visible"] = hidden ? "false" : "true",
        });
        await _fb.SetDocAsync($"servers/{serverId}/admin/secret", new Dictionary<string, object> { ["password"] = password });
        await _fb.SetDocAsync($"sessions/{serverId}/access/{hash}", new Dictionary<string, object> { ["address"] = "" });
    }
}
