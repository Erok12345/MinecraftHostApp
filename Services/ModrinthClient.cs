using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MinecraftHostApp.Services;

public class ModSummary
{
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public string Author { get; set; } = "";
    public int Downloads { get; set; }
    // "required" / "optional" / "unsupported" - по этому полю понятно, сломает ли мод
    // выделенный сервер (нужен только клиенту) или клиент (нужен только серверу).
    public string ClientSide { get; set; } = "unknown";
    public string ServerSide { get; set; } = "unknown";
    public bool IsServerUnsupported => ServerSide == "unsupported";
    public bool IsClientUnsupported => ClientSide == "unsupported";
}

public class ModVersionInfo
{
    public string VersionNumber { get; set; } = "";
    public string FileUrl { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime Published { get; set; }
}

public class ModDependency
{
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string DependencyType { get; set; } = ""; // required / optional / incompatible / embedded
}

public class ModSearchResult
{
    public List<ModSummary> Hits { get; set; } = new();
    public int TotalHits { get; set; }
}

public class ModrinthClient
{
    private readonly HttpClient _http = new();

    public ModrinthClient()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MinecraftHostApp/1.0 (rotating-host-friend-tool)");
    }

    public async Task<ModSearchResult> SearchAsync(string query, string loader, string mcVersion, int offset = 0, int limit = 20)
    {
        var facets = $"[[\"project_type:mod\"],[\"categories:{loader}\"],[\"versions:{mcVersion}\"]]";
        // index=relevance при пустом запросе Modrinth трактует как "самое популярное" - то, что нужно для дефолтного списка.
        var index = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";
        var url = $"https://api.modrinth.com/v2/search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}&index={index}&offset={offset}&limit={limit}";
        var resp = await _http.GetFromJsonAsync<JsonObject>(url);
        var result = new ModSearchResult { TotalHits = resp?["total_hits"]?.GetValue<int>() ?? 0 };
        if (resp?["hits"] is JsonArray hits)
        {
            foreach (var hit in hits)
            {
                result.Hits.Add(new ModSummary
                {
                    ProjectId = hit?["project_id"]?.ToString() ?? hit?["slug"]?.ToString() ?? "",
                    Title = hit?["title"]?.ToString() ?? "",
                    Description = hit?["description"]?.ToString() ?? "",
                    IconUrl = hit?["icon_url"]?.ToString() ?? "",
                    Author = hit?["author"]?.ToString() ?? "",
                    Downloads = hit?["downloads"]?.GetValue<int>() ?? 0,
                    ClientSide = hit?["client_side"]?.ToString() ?? "unknown",
                    ServerSide = hit?["server_side"]?.ToString() ?? "unknown",
                });
            }
        }
        return result;
    }

    // Все версии под конкретные загрузчик+версию Minecraft, от новой к старой - чтобы можно было
    // выбрать не только последнюю, но и любую другую (например если новая версия мода сломала мир).
    public async Task<List<ModVersionInfo>> GetVersionsAsync(string projectId, string loader, string mcVersion)
    {
        var url = $"https://api.modrinth.com/v2/project/{projectId}/version?loaders=[\"{loader}\"]&game_versions=[\"{mcVersion}\"]";
        var versions = await _http.GetFromJsonAsync<JsonArray>(url);
        var result = new List<ModVersionInfo>();
        if (versions == null) return result;

        foreach (var v in versions)
        {
            if (v?["files"] is not JsonArray files) continue;
            var primary = files.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true) ?? files.FirstOrDefault();
            if (primary == null) continue;

            result.Add(new ModVersionInfo
            {
                VersionNumber = v["version_number"]?.ToString() ?? "",
                FileUrl = primary["url"]!.ToString(),
                FileName = primary["filename"]!.ToString(),
                Published = DateTime.TryParse(v["date_published"]?.ToString(), out var pub) ? pub : DateTime.MinValue,
            });
        }
        return result.OrderByDescending(v => v.Published).ToList();
    }

    public async Task<ModVersionInfo?> GetLatestVersionAsync(string projectId, string loader, string mcVersion) =>
        (await GetVersionsAsync(projectId, loader, mcVersion)).FirstOrDefault();

    public async Task<byte[]> DownloadBytesAsync(string url) => await _http.GetByteArrayAsync(url);

    // Обобщённый поиск для типов контента без загрузчика (ресурспаки, датапаки - им Fabric/Forge не важен).
    public async Task<ModSearchResult> SearchContentAsync(string projectType, string query, string mcVersion, int offset = 0, int limit = 20)
    {
        var facets = $"[[\"project_type:{projectType}\"],[\"versions:{mcVersion}\"]]";
        var index = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";
        var url = $"https://api.modrinth.com/v2/search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}&index={index}&offset={offset}&limit={limit}";
        var resp = await _http.GetFromJsonAsync<JsonObject>(url);
        var result = new ModSearchResult { TotalHits = resp?["total_hits"]?.GetValue<int>() ?? 0 };
        if (resp?["hits"] is JsonArray hits)
        {
            foreach (var hit in hits)
            {
                result.Hits.Add(new ModSummary
                {
                    ProjectId = hit?["project_id"]?.ToString() ?? hit?["slug"]?.ToString() ?? "",
                    Title = hit?["title"]?.ToString() ?? "",
                    Description = hit?["description"]?.ToString() ?? "",
                    IconUrl = hit?["icon_url"]?.ToString() ?? "",
                    Author = hit?["author"]?.ToString() ?? "",
                    Downloads = hit?["downloads"]?.GetValue<int>() ?? 0,
                });
            }
        }
        return result;
    }

    // Все версии контента без загрузчика (ресурспаки, датапаки), от новой к старой - чтобы можно
    // было поставить не только последнюю, но и любую другую (как со списком версий у модов).
    public async Task<List<ModVersionInfo>> GetContentVersionsAsync(string projectId, string mcVersion)
    {
        var url = $"https://api.modrinth.com/v2/project/{projectId}/version?game_versions=[\"{mcVersion}\"]";
        var versions = await _http.GetFromJsonAsync<JsonArray>(url);
        var result = new List<ModVersionInfo>();
        if (versions == null) return result;

        foreach (var v in versions)
        {
            if (v?["files"] is not JsonArray files) continue;
            var primary = files.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true) ?? files.FirstOrDefault();
            if (primary == null) continue;

            result.Add(new ModVersionInfo
            {
                VersionNumber = v["version_number"]?.ToString() ?? "",
                FileUrl = primary["url"]!.ToString(),
                FileName = primary["filename"]!.ToString(),
                Published = DateTime.TryParse(v["date_published"]?.ToString(), out var pub) ? pub : DateTime.MinValue,
            });
        }
        return result.OrderByDescending(v => v.Published).ToList();
    }

    // Последняя версия под версию Minecraft, без привязки к загрузчику.
    public async Task<ModVersionInfo?> GetLatestContentVersionAsync(string projectId, string mcVersion)
    {
        var url = $"https://api.modrinth.com/v2/project/{projectId}/version?game_versions=[\"{mcVersion}\"]";
        var versions = await _http.GetFromJsonAsync<JsonArray>(url);
        if (versions == null) return null;

        ModVersionInfo? best = null;
        var bestDate = DateTime.MinValue;
        foreach (var v in versions)
        {
            if (v?["files"] is not JsonArray files) continue;
            var primary = files.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true) ?? files.FirstOrDefault();
            if (primary == null) continue;
            var published = DateTime.TryParse(v["date_published"]?.ToString(), out var pub) ? pub : DateTime.MinValue;
            if (best != null && published <= bestDate) continue;
            best = new ModVersionInfo
            {
                VersionNumber = v["version_number"]?.ToString() ?? "",
                FileUrl = primary["url"]!.ToString(),
                FileName = primary["filename"]!.ToString(),
                Published = published,
            };
            bestDate = published;
        }
        return best;
    }

    // Для карточки мода, открытой из уже установленного списка (там под рукой нет данных поиска) -
    // подтягиваем сведения о проекте напрямую.
    public async Task<ModSummary?> GetProjectSummaryAsync(string projectId)
    {
        var proj = await _http.GetFromJsonAsync<JsonObject>($"https://api.modrinth.com/v2/project/{projectId}");
        if (proj == null) return null;
        return new ModSummary
        {
            ProjectId = proj["id"]?.ToString() ?? proj["slug"]?.ToString() ?? projectId,
            Title = proj["title"]?.ToString() ?? projectId,
            Description = proj["description"]?.ToString() ?? "",
            IconUrl = proj["icon_url"]?.ToString() ?? "",
            ClientSide = proj["client_side"]?.ToString() ?? "unknown",
            ServerSide = proj["server_side"]?.ToString() ?? "unknown",
        };
    }

    // Список зависимостей проекта ("что понадобится") - как на странице мода на самом Modrinth.
    public async Task<List<ModDependency>> GetDependenciesAsync(string projectId)
    {
        var resp = await _http.GetFromJsonAsync<JsonObject>($"https://api.modrinth.com/v2/project/{projectId}/dependencies");
        var result = new List<ModDependency>();
        if (resp?["projects"] is not JsonArray projects) return result;

        var typeByProjectId = new Dictionary<string, string>();
        if (resp["dependencies"] is JsonArray deps)
        {
            foreach (var dep in deps)
            {
                var pid = dep?["project_id"]?.ToString();
                var type = dep?["dependency_type"]?.ToString() ?? "required";
                if (pid != null) typeByProjectId[pid] = type;
            }
        }

        foreach (var proj in projects)
        {
            var pid = proj?["id"]?.ToString() ?? "";
            if (pid == projectId) continue;
            result.Add(new ModDependency
            {
                ProjectId = pid,
                Title = proj?["title"]?.ToString() ?? pid,
                DependencyType = typeByProjectId.GetValueOrDefault(pid, "required"),
            });
        }
        return result;
    }
}
