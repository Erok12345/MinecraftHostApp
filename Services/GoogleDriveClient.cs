using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using MinecraftHostApp.Models;

namespace MinecraftHostApp.Services;

// Сессия Google протухла (обычная история, пока приложение в статусе "Testing" - раз в 7 дней) -
// вызывающий код должен молча вызвать ConnectAsync() ещё раз и повторить операцию.
public class GoogleReauthRequiredException : Exception
{
    public GoogleReauthRequiredException(string message) : base(message) { }
}

// OAuth-вход и базовые операции с Google Drive (папка на пользователя, загрузка/скачивание файлов,
// выдача доступа по email) - замена ручной установке Google Drive для компьютеров и шаринга через сайт.
public class GoogleDriveClient
{
    private readonly AppConfig _config;
    private readonly HttpClient _http = new();
    private string? _accessToken;
    private DateTime _accessTokenExpiry;

    public GoogleDriveClient(AppConfig config) => _config = config;

    public bool IsConnected => !string.IsNullOrWhiteSpace(_config.GoogleRefreshToken);

    // Открывает браузер для входа через Google, ждёт редирект на локальный порт, обменивает код
    // на refresh token и сохраняет его в конфиг (см. AppConfig.GoogleRefreshToken).
    public async Task<string> ConnectAsync()
    {
        var port = GetFreePort();
        var redirectUri = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var state = Guid.NewGuid().ToString("N");
        var authUrl = "https://accounts.google.com/o/oauth2/v2/auth" +
            $"?client_id={Uri.EscapeDataString(_config.GoogleClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&response_type=code" +
            $"&scope={Uri.EscapeDataString("https://www.googleapis.com/auth/drive.file")}" +
            "&access_type=offline" +
            "&prompt=consent" +
            $"&state={state}";

        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        var contextTask = listener.GetContextAsync();
        var completed = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(3)));
        if (completed != contextTask)
        {
            listener.Stop();
            throw new Exception("Не дождались входа через Google (истекло время ожидания).");
        }

        var context = contextTask.Result;
        var code = context.Request.QueryString["code"];
        var returnedState = context.Request.QueryString["state"];

        var html = "<html><body style=\"font-family:sans-serif;text-align:center;padding-top:60px\">" +
                   "<h2>Готово! Можно закрыть эту вкладку и вернуться в приложение.</h2></body></html>";
        var buffer = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer);
        context.Response.OutputStream.Close();
        listener.Stop();

        if (string.IsNullOrEmpty(code) || returnedState != state)
            throw new Exception("Google не вернул код авторизации - вход отменён или произошла ошибка.");

        var tokenResp = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _config.GoogleClientId,
            ["client_secret"] = _config.GoogleClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        }));
        var json = await tokenResp.Content.ReadFromJsonAsync<JsonObject>();
        if (!tokenResp.IsSuccessStatusCode)
            throw new Exception($"Google отклонил вход: {json?["error_description"]?.ToString() ?? json?["error"]?.ToString()}");

        var refreshToken = json?["refresh_token"]?.ToString();
        if (string.IsNullOrEmpty(refreshToken))
            throw new Exception("Google не выдал refresh token. Попробуй ещё раз, или сначала убери старый доступ " +
                                 "на myaccount.google.com/permissions и повтори вход.");

        _config.GoogleRefreshToken = refreshToken;
        _config.Save();

        _accessToken = json!["access_token"]!.ToString();
        _accessTokenExpiry = DateTime.UtcNow.AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3600).AddSeconds(-30);

        return "Успешно подключено к Google Drive.";
    }

    public void Disconnect()
    {
        _config.GoogleRefreshToken = "";
        _config.GoogleDriveFolderId = "";
        _config.Save();
        _accessToken = null;
    }

    private async Task EnsureAccessTokenAsync()
    {
        if (_accessToken != null && DateTime.UtcNow < _accessTokenExpiry) return;
        if (string.IsNullOrWhiteSpace(_config.GoogleRefreshToken))
            throw new GoogleReauthRequiredException("Сначала нужно войти через Google.");

        var resp = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["refresh_token"] = _config.GoogleRefreshToken,
            ["client_id"] = _config.GoogleClientId,
            ["client_secret"] = _config.GoogleClientSecret,
            ["grant_type"] = "refresh_token",
        }));
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        if (!resp.IsSuccessStatusCode)
        {
            // Пока приложение в режиме "Testing" - Google сам аннулирует refresh token примерно раз
            // в 7 дней. Чистим его и просим вызывающий код молча повторить ConnectAsync().
            _config.GoogleRefreshToken = "";
            _config.Save();
            throw new GoogleReauthRequiredException(
                json?["error_description"]?.ToString() ?? "Сессия Google истекла, нужно войти заново.");
        }
        _accessToken = json!["access_token"]!.ToString();
        _accessTokenExpiry = DateTime.UtcNow.AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3600).AddSeconds(-30);
    }

    public async Task<string> EnsureRootFolderAsync()
    {
        if (!string.IsNullOrWhiteSpace(_config.GoogleDriveFolderId)) return _config.GoogleDriveFolderId;
        var id = await FindOrCreateFolderAsync("MinecraftRotatingHost");
        _config.GoogleDriveFolderId = id;
        _config.Save();
        return id;
    }

    public async Task<string> FindOrCreateFolderAsync(string name, string? parentId = null)
    {
        var parentClause = parentId != null ? $" and '{parentId}' in parents" : "";
        var q = Uri.EscapeDataString($"name='{EscapeForQuery(name)}' and mimeType='application/vnd.google-apps.folder' and trashed=false{parentClause}");
        var found = await GetAsync($"https://www.googleapis.com/drive/v3/files?q={q}&fields=files(id,name)");
        if (found["files"] is JsonArray arr && arr.Count > 0) return arr[0]!["id"]!.ToString();

        var metadata = new JsonObject { ["name"] = name, ["mimeType"] = "application/vnd.google-apps.folder" };
        if (parentId != null) metadata["parents"] = new JsonArray { parentId };
        var created = await PostJsonAsync("https://www.googleapis.com/drive/v3/files", metadata);
        return created["id"]!.ToString();
    }

    public async Task<List<(string Id, string Name, DateTime Modified)>> ListFilesAsync(string folderId, string? nameContains = null)
    {
        var q = $"'{folderId}' in parents and trashed=false" +
                (nameContains != null ? $" and name contains '{EscapeForQuery(nameContains)}'" : "");
        var json = await GetAsync($"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=files(id,name,modifiedTime)&orderBy=modifiedTime desc");
        var result = new List<(string, string, DateTime)>();
        if (json["files"] is JsonArray arr)
            foreach (var f in arr)
                result.Add((f!["id"]!.ToString(), f["name"]!.ToString(),
                    DateTime.TryParse(f["modifiedTime"]?.ToString(), out var d) ? d : DateTime.MinValue));
        return result;
    }

    // uploadType=resumable - работает и для маленьких, и для больших файлов (миры серверов бывают
    // сотни МБ, а простая multipart-загрузка Google Drive ограничена 5 МБ).
    public async Task<string> UploadFileAsync(string folderId, string fileName, byte[] bytes, string? existingFileId = null)
    {
        await EnsureAccessTokenAsync();
        var url = existingFileId != null
            ? $"https://www.googleapis.com/upload/drive/v3/files/{existingFileId}?uploadType=resumable"
            : "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable";
        var method = existingFileId != null ? HttpMethod.Patch : HttpMethod.Post;

        var metadata = new JsonObject { ["name"] = fileName };
        if (existingFileId == null) metadata["parents"] = new JsonArray { folderId };

        var initReq = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(metadata.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        initReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var initResp = await _http.SendAsync(initReq);
        if (!initResp.IsSuccessStatusCode)
            throw new Exception($"Drive upload init: {await initResp.Content.ReadAsStringAsync()}");
        var uploadUrl = initResp.Headers.Location?.ToString()
            ?? throw new Exception("Drive не вернул адрес для загрузки файла.");

        using var body = new ByteArrayContent(bytes);
        var putResp = await _http.PutAsync(uploadUrl, body);
        var json = await putResp.Content.ReadFromJsonAsync<JsonObject>();
        if (!putResp.IsSuccessStatusCode)
            throw new Exception($"Drive upload: {json?["error"]?["message"]?.ToString() ?? putResp.StatusCode.ToString()}");
        return json!["id"]!.ToString();
    }

    public async Task<byte[]> DownloadFileAsync(string fileId)
    {
        await EnsureAccessTokenAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Drive download: {resp.StatusCode}");
        return await resp.Content.ReadAsByteArrayAsync();
    }

    public async Task DeleteFileAsync(string fileId)
    {
        await EnsureAccessTokenAsync();
        var req = new HttpRequestMessage(HttpMethod.Delete, $"https://www.googleapis.com/drive/v3/files/{fileId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        await _http.SendAsync(req);
    }

    // Переносит файл в другую папку без перекачивания байтов (просто меняет родителя).
    public async Task MoveFileAsync(string fileId, string newParentId, string oldParentId)
    {
        await EnsureAccessTokenAsync();
        var url = $"https://www.googleapis.com/drive/v3/files/{fileId}?addParents={newParentId}&removeParents={oldParentId}";
        var req = new HttpRequestMessage(HttpMethod.Patch, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Drive move: {await resp.Content.ReadAsStringAsync()}");
    }

    // Выдаёт доступ конкретному email прямо из приложения - замена ручному "Открыть доступ" на сайте Drive.
    public async Task ShareWithEmailAsync(string fileOrFolderId, string email, string role = "writer")
    {
        var body = new JsonObject { ["type"] = "user", ["role"] = role, ["emailAddress"] = email };
        await PostJsonAsync($"https://www.googleapis.com/drive/v3/files/{fileOrFolderId}/permissions?sendNotificationEmail=true", body);
    }

    private async Task<JsonObject> GetAsync(string url)
    {
        await EnsureAccessTokenAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var resp = await _http.SendAsync(req);
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Drive API: {json?["error"]?["message"]?.ToString() ?? resp.StatusCode.ToString()}");
        return json!;
    }

    private async Task<JsonObject> PostJsonAsync(string url, JsonObject body)
    {
        await EnsureAccessTokenAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var resp = await _http.SendAsync(req);
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Drive API: {json?["error"]?["message"]?.ToString() ?? resp.StatusCode.ToString()}");
        return json!;
    }

    private static string EscapeForQuery(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
