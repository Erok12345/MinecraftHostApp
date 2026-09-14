using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinecraftHostApp.Models;

namespace MinecraftHostApp.Services;

public class FirestoreDoc
{
    public bool Exists { get; set; }
    public Dictionary<string, object> Fields { get; set; } = new();

    public string GetString(string key) => Fields.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
    public List<string> GetArray(string key) =>
        Fields.TryGetValue(key, out var v) && v is List<string> list ? list : new List<string>();
}

public class ServerInfo
{
    public string Id { get; set; } = "";
    public FirestoreDoc Doc { get; set; } = new();
}

public class FirebaseClient
{
    private readonly HttpClient _http = new();
    private readonly AppConfig _config;
    public string? IdToken { get; private set; }
    public string? Uid { get; private set; }

    public FirebaseClient(AppConfig config) => _config = config;

    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string FriendlyAuthError(string rawCode) => rawCode switch
    {
        "EMAIL_NOT_FOUND" => "Такого аккаунта нет. Нажми 'Зарегистрироваться', если ты здесь впервые.",
        "INVALID_PASSWORD" => "Неверный пароль.",
        "INVALID_LOGIN_CREDENTIALS" => "Неверный email или пароль.",
        "EMAIL_EXISTS" => "Этот email уже зарегистрирован — просто войди вместо регистрации.",
        "WEAK_PASSWORD : Password should be at least 6 characters" => "Пароль слишком короткий (минимум 6 символов).",
        "INVALID_EMAIL" => "Некорректный email.",
        "USER_DISABLED" => "Этот аккаунт отключён администратором.",
        _ when rawCode.StartsWith("WEAK_PASSWORD") => "Пароль слишком короткий (минимум 6 символов).",
        _ when rawCode.StartsWith("TOO_MANY_ATTEMPTS") => "Слишком много попыток - подожди немного и попробуй снова.",
        _ => $"Ошибка: {rawCode}",
    };

    private async Task<(bool ok, string error)> AuthRequestAsync(string endpoint, string email, string password)
    {
        var url = $"https://identitytoolkit.googleapis.com/v1/accounts:{endpoint}?key={_config.FirebaseApiKey}";
        var body = new { email, password, returnSecureToken = true };
        var resp = await _http.PostAsJsonAsync(url, body);
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        if (!resp.IsSuccessStatusCode)
        {
            var code = json?["error"]?["message"]?.ToString() ?? "неизвестная ошибка";
            return (false, FriendlyAuthError(code));
        }
        IdToken = json?["idToken"]?.ToString();
        Uid = json?["localId"]?.ToString();
        return (true, "");
    }

    public Task<(bool ok, string error)> SignInAsync() => AuthRequestAsync("signInWithPassword", _config.Email, _config.Password);

    public Task<(bool ok, string error)> SignUpAsync() => AuthRequestAsync("signUp", _config.Email, _config.Password);

    public async Task SendEmailVerificationAsync()
    {
        var url = $"https://identitytoolkit.googleapis.com/v1/accounts:sendOobCode?key={_config.FirebaseApiKey}";
        var body = new { requestType = "VERIFY_EMAIL", idToken = IdToken };
        await _http.PostAsJsonAsync(url, body);
    }

    public async Task SetDisplayNameAsync(string displayName)
    {
        var url = $"https://identitytoolkit.googleapis.com/v1/accounts:update?key={_config.FirebaseApiKey}";
        var body = new { idToken = IdToken, displayName, returnSecureToken = false };
        await _http.PostAsJsonAsync(url, body);
        // Публикуем в общий каталог users/{uid}, чтобы другие могли увидеть твой ник (автор сервера, участники).
        await SetDocAsync($"users/{Uid}", new Dictionary<string, object> { ["displayName"] = displayName });
    }

    public async Task<string> GetPublicDisplayNameAsync(string uid)
    {
        var doc = await GetDocAsync($"users/{uid}");
        var name = doc.GetString("displayName");
        return string.IsNullOrWhiteSpace(name) ? uid : name;
    }

    // "everyone" (по умолчанию) / "friends" / "nobody" - кто видит список серверов этого человека на его профиле.
    public async Task<string> GetServersPrivacyAsync(string uid)
    {
        var doc = await GetDocAsync($"users/{uid}");
        var privacy = doc.GetString("privacyServers");
        return string.IsNullOrEmpty(privacy) ? "everyone" : privacy;
    }

    public async Task SetServersPrivacyAsync(string privacy) =>
        await SetDocAsync($"users/{Uid}", new Dictionary<string, object> { ["privacyServers"] = privacy });

    // "друзья" тут - те, кого владелец профиля сам добавил (доступ дают, а не запрашивают - как список "поделиться с").
    public async Task<bool> CanViewServersAsync(string targetUid)
    {
        if (targetUid == Uid) return true;
        var privacy = await GetServersPrivacyAsync(targetUid);
        if (privacy == "everyone") return true;
        if (privacy == "nobody") return false;
        var doc = await GetDocAsync($"users/{targetUid}");
        return doc.GetArray("friends").Contains(Uid!);
    }

    public async Task<List<string>> GetFriendsAsync()
    {
        var doc = await GetDocAsync($"users/{Uid}");
        return doc.GetArray("friends");
    }

    public async Task AddFriendAsync(string friendUid)
    {
        var doc = await GetDocAsync($"users/{Uid}");
        var current = doc.GetArray("friends");
        if (!current.Contains(friendUid)) current.Add(friendUid);
        await SetDocAsync($"users/{Uid}", new Dictionary<string, object> { ["friends"] = current });
    }

    public async Task RemoveFriendAsync(string friendUid)
    {
        var doc = await GetDocAsync($"users/{Uid}");
        var current = doc.GetArray("friends").Where(u => u != friendUid).ToList();
        await SetDocAsync($"users/{Uid}", new Dictionary<string, object> { ["friends"] = current });
    }

    public async Task SetAvatarUrlAsync(string url) =>
        await SetDocAsync($"users/{Uid}", new Dictionary<string, object> { ["avatarUrl"] = url });

    public async Task<string> GetAvatarUrlAsync(string uid)
    {
        var doc = await GetDocAsync($"users/{uid}");
        return doc.GetString("avatarUrl");
    }

    // Список серверов, в которые человек сам когда-либо заходил (по паролю) - это и есть
    // "список серверов" на его профиле, который прячется настройкой приватности выше.
    public async Task AddJoinedServerAsync(string serverId)
    {
        var doc = await GetDocAsync($"users/{Uid}");
        var current = doc.GetArray("joinedServers");
        if (!current.Contains(serverId)) current.Add(serverId);
        await SetDocAsync($"users/{Uid}", new Dictionary<string, object> { ["joinedServers"] = current });
    }

    public async Task<List<string>> GetJoinedServersAsync(string uid)
    {
        var doc = await GetDocAsync($"users/{uid}");
        return doc.GetArray("joinedServers");
    }

    public async Task<string> GetDisplayNameAsync()
    {
        var url = $"https://identitytoolkit.googleapis.com/v1/accounts:lookup?key={_config.FirebaseApiKey}";
        var body = new { idToken = IdToken };
        var resp = await _http.PostAsJsonAsync(url, body);
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        return json?["users"]?[0]?["displayName"]?.ToString() ?? "";
    }

    private string Base => $"https://firestore.googleapis.com/v1/projects/{_config.FirebaseProjectId}/databases/(default)/documents";

    private HttpRequestMessage Authed(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Authorization", $"Bearer {IdToken}");
        return req;
    }

    public async Task<FirestoreDoc> GetDocAsync(string path)
    {
        var resp = await _http.SendAsync(Authed(HttpMethod.Get, $"{Base}/{path}"));
        if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Forbidden)
            return new FirestoreDoc { Exists = false };
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        return new FirestoreDoc { Exists = true, Fields = ParseFields(json?["fields"] as JsonObject) };
    }

    public async Task<List<ServerInfo>> ListCollectionAsync(string collection)
    {
        var resp = await _http.SendAsync(Authed(HttpMethod.Get, $"{Base}/{collection}"));
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        var result = new List<ServerInfo>();
        if (json?["documents"] is JsonArray docs)
        {
            foreach (var doc in docs)
            {
                var name = doc?["name"]?.ToString() ?? "";
                var id = name.Split('/').Last();
                result.Add(new ServerInfo
                {
                    Id = id,
                    Doc = new FirestoreDoc { Exists = true, Fields = ParseFields(doc?["fields"] as JsonObject) }
                });
            }
        }
        return result;
    }

    public async Task SetDocAsync(string path, Dictionary<string, object> fields)
    {
        var body = new JsonObject { ["fields"] = BuildFields(fields) };
        var mask = string.Join("&", fields.Keys.Select(k => $"updateMask.fieldPaths={Uri.EscapeDataString(k)}"));
        var url = $"{Base}/{path}?{mask}";
        var req = Authed(HttpMethod.Patch, url);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteDocAsync(string path)
    {
        var resp = await _http.SendAsync(Authed(HttpMethod.Delete, $"{Base}/{path}"));
        resp.EnsureSuccessStatusCode();
    }

    private static JsonObject BuildFields(Dictionary<string, object> fields)
    {
        var obj = new JsonObject();
        foreach (var (key, val) in fields)
        {
            if (val is IEnumerable<string> arr)
            {
                var values = new JsonArray();
                foreach (var s in arr) values.Add(new JsonObject { ["stringValue"] = s });
                obj[key] = new JsonObject { ["arrayValue"] = new JsonObject { ["values"] = values } };
            }
            else
            {
                obj[key] = new JsonObject { ["stringValue"] = val?.ToString() ?? "" };
            }
        }
        return obj;
    }

    private static Dictionary<string, object> ParseFields(JsonObject? fields)
    {
        var result = new Dictionary<string, object>();
        if (fields == null) return result;
        foreach (var (key, val) in fields)
        {
            if (val == null) continue;
            if (val["stringValue"] != null)
                result[key] = val["stringValue"]!.ToString();
            else if (val["arrayValue"] != null)
            {
                var list = new List<string>();
                if (val["arrayValue"]?["values"] is JsonArray values)
                    foreach (var v in values)
                        if (v?["stringValue"] != null) list.Add(v["stringValue"]!.ToString());
                result[key] = list;
            }
        }
        return result;
    }
}
