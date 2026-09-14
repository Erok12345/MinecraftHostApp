using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MinecraftHostApp.Services;

public class ImgBbClient
{
    private readonly HttpClient _http = new();
    private readonly string _apiKey;

    public ImgBbClient(string apiKey) => _apiKey = apiKey;

    public async Task<string> UploadAsync(byte[] imageBytes)
    {
        var base64 = Convert.ToBase64String(imageBytes);
        var form = new Dictionary<string, string> { ["key"] = _apiKey, ["image"] = base64 };
        var resp = await _http.PostAsync("https://api.imgbb.com/1/upload", new FormUrlEncodedContent(form));
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        if (!resp.IsSuccessStatusCode)
        {
            var msg = json?["error"]?["message"]?.ToString() ?? $"HTTP {(int)resp.StatusCode}";
            throw new Exception($"ImgBB: {msg}");
        }
        return json?["data"]?["url"]?.ToString() ?? throw new Exception("ImgBB не вернул ссылку на фото.");
    }
}
