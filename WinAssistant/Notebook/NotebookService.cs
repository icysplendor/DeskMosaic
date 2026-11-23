using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinAssistant.Settings;

namespace WinAssistant.Notebook;

// The shared editor never receives arbitrary filesystem access or the WebDAV secret.
internal sealed class NotebookService
{
    private readonly string configFolder;
    private static readonly string DefaultConfigFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinAssistant", "Notebook");
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
    private readonly SemaphoreSlim gate = new(1);
    private NotebookPreferences preferences;
    public string Root => preferences.Root;
    public NotebookService(string? configurationFolder = null, string? defaultRoot = null)
    {
        configFolder = configurationFolder ?? DefaultConfigFolder;
        Directory.CreateDirectory(configFolder);
        preferences = LoadPreferences();
        if (defaultRoot is not null && !System.IO.File.Exists(Path.Combine(configFolder, "preferences.json"))) preferences.Root = defaultRoot;
        Directory.CreateDirectory(Root);
    }
    private NotebookPreferences LoadPreferences()
    {
        foreach (var suffix in new[] { "", ".bak" })
        {
            try { var file = Path.Combine(configFolder, "preferences.json" + suffix); if (System.IO.File.Exists(file)) { var data = System.IO.File.ReadAllBytes(file); var recovered = JsonSerializer.Deserialize<NotebookPreferences>(data) ?? new(); if (suffix.Length > 0) AtomicWrite(Path.Combine(configFolder, "preferences.json"), data, false); return recovered; } }
            catch (JsonException) { }
        }
        return new();
    }
    private void SavePreferences() => AtomicWrite(Path.Combine(configFolder, "preferences.json"), JsonSerializer.SerializeToUtf8Bytes(preferences));
    public object Settings() => new { platform = "windows", nativeTitlebar = true, theme = preferences.Theme ?? ConfigurationStore.Shared.Configuration.Audio.Theme, url = preferences.Url, user = preferences.User, syncEnabled = preferences.Enabled };
    public void SetTheme(string value) { preferences.Theme = value == "Dark" ? "Dark" : "Light"; SavePreferences(); }
    public string Resolve(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(x => x is "." or "..")) throw new IOException("笔记路径无效");
        var root = Path.GetFullPath(Root); var path = Path.GetFullPath(Path.Combine(root, relative));
        if (path != root && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("路径超出笔记库");
        var cursor = path;
        while (cursor.Length >= root.Length)
        {
            if ((Directory.Exists(cursor) || System.IO.File.Exists(cursor)) && (System.IO.File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("笔记库不支持符号链接或目录联接");
            if (cursor == root) break; cursor = Path.GetDirectoryName(cursor)!;
        }
        return path;
    }
    public static void AtomicWrite(string path, byte[] bytes, bool createBackup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if (createBackup && System.IO.File.Exists(path)) System.IO.File.Copy(path, path + ".bak", true);
            System.IO.File.Move(temporary, path, true);
        }
        finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }
    private string? Read(string path)
    {
        if (!System.IO.File.Exists(path)) return null;
        var text = System.IO.File.ReadAllText(path, Encoding.UTF8);
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try { using var ignored = JsonDocument.Parse(text); }
            catch (JsonException) when (System.IO.File.Exists(path + ".bak"))
            { text = System.IO.File.ReadAllText(path + ".bak", Encoding.UTF8); using var ignored = JsonDocument.Parse(text); AtomicWrite(path, Encoding.UTF8.GetBytes(text), false); }
        }
        return text;
    }
    public async Task<object?> ExecuteAsync(string op, JsonElement args)
    {
        string S(string name) => args.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        // WebDAV waits must not hold the local-file gate. The shared sync store
        // still serializes filesystem mutations, matching macOS actor reentrancy
        // while URLSession awaits a remote response.
        if (op == "http")
            return await RequestAsync(preferences, S("method"), S("path"), args.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String ? data.GetString() : null, args.TryGetProperty("headers", out var headers) ? headers : default);
        await gate.WaitAsync();
        try
        {
            switch (op)
            {
                case "read": Resolve(S("path") + ".bak"); return Read(Resolve(S("path")));
                case "readBinary": { var path = Resolve(S("path")); return System.IO.File.Exists(path) ? Convert.ToBase64String(await System.IO.File.ReadAllBytesAsync(path)) : null; }
                case "write": { Resolve(S("path") + ".bak"); var path = Resolve(S("path")); AtomicWrite(path, Convert.FromBase64String(S("data"))); return null; }
                case "mkdir": Directory.CreateDirectory(Resolve(S("path"))); return null;
                case "list": { var path = Resolve(S("path")); return Directory.Exists(path) ? Directory.EnumerateFileSystemEntries(path).Where(x => (System.IO.File.GetAttributes(x) & FileAttributes.ReparsePoint) == 0).Select(x => new { name = Path.GetFileName(x), dir = Directory.Exists(x), modified = new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(x)).ToUnixTimeMilliseconds() }).ToArray() : []; }
                case "move": { var source = Resolve(S("path")); var destination = Resolve(S("to")); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); if (Directory.Exists(source)) Directory.Move(source, destination); else System.IO.File.Move(source, destination); return null; }
                case "remove": { var path = Resolve(S("path")); if (Directory.Exists(path)) throw new IOException("不允许递归永久删除笔记目录"); if (System.IO.File.Exists(path)) System.IO.File.Delete(path); if (System.IO.File.Exists(path + ".bak")) System.IO.File.Delete(path + ".bak"); return null; }
                case "purgeDeleted": {
                    var relative = S("path");
                    if (!System.Text.RegularExpressions.Regex.IsMatch(relative, @"^\.(deleted|deleted-books)/[A-Za-z0-9_-]+$")) throw new IOException("只能永久删除回收站中的笔记或笔记本");
                    var path = Resolve(relative);
                    if (!Directory.Exists(path)) return null;
                    var folders = new Queue<string>(); folders.Enqueue(path);
                    while (folders.TryDequeue(out var folder)) foreach (var entry in Directory.EnumerateFileSystemEntries(folder)) {
                        if ((System.IO.File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("回收目录包含符号链接或目录联接，已停止删除");
                        if (Directory.Exists(entry)) folders.Enqueue(entry);
                    }
                    Directory.Delete(path, true); return null;
                }
                default: throw new InvalidOperationException("未知笔记服务操作");
            }
        }
        finally { gate.Release(); }
    }
    public async Task<object> ConfigureAsync(JsonElement args)
    {
        var next = new NotebookPreferences { Root = preferences.Root, Theme = preferences.Theme, Url = args.GetProperty("url").GetString()!.Trim(), User = args.GetProperty("user").GetString()!.Trim(), Enabled = args.GetProperty("enabled").GetBoolean(), Secret = preferences.Secret };
        var password = args.GetProperty("password").GetString();
        if (next.Url != preferences.Url || next.User != preferences.User) next.Secret = "";
        if (!string.IsNullOrEmpty(password)) next.Secret = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
        if (next.Enabled)
        {
            if (!Uri.TryCreate(next.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new IOException("请输入 HTTPS WebDAV 文件夹地址");
            next.Url = uri.AbsoluteUri.TrimEnd('/') + "/";
            if (string.IsNullOrEmpty(next.User) || string.IsNullOrEmpty(next.Secret)) throw new IOException("请输入用户名和应用密码");
            var result = await RequestAsync(next, "PROPFIND", "", null, JsonSerializer.SerializeToElement(new { Depth = "0" }));
            if (result.status != 207) throw new IOException($"WebDAV 连接校验失败 ({result.status})");
        }
        // Changing the target resets the sync baseline, not the local notes.
        if (next.Url != preferences.Url || next.User != preferences.User)
        {
            var old = Resolve(".sync.json"); if (System.IO.File.Exists(old)) System.IO.File.Move(old, old + ".previous-" + Guid.NewGuid().ToString("N"));
        }
        preferences = next; SavePreferences(); return Settings();
    }
    private async Task<DAVResponse> RequestAsync(NotebookPreferences config, string method, string relative, string? body, JsonElement headers)
    {
        if (!new[] { "GET", "PUT", "DELETE", "PROPFIND", "MKCOL" }.Contains(method)) throw new IOException("不支持的同步请求");
        if (relative.StartsWith('/') || relative.Contains('\\') || relative.Split('/').Any(x => x is "." or "..")) throw new IOException("在线路径无效");
        var baseUri = new Uri(config.Url); var encodedPath = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        var uri = new Uri(baseUri, encodedPath);
        if (uri.Scheme != "https" || uri.Host != baseUri.Host || !uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal)) throw new IOException("同步请求超出已配置目录");
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        // Keep GET ETags consistent with conditional WebDAV writes and lock release.
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        var secret = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(config.Secret), null, DataProtectionScope.CurrentUser));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(config.User + ":" + secret)));
        if (headers.ValueKind == JsonValueKind.Object) foreach (var header in headers.EnumerateObject()) if (new[] { "Depth", "If-Match", "If-None-Match" }.Contains(header.Name)) request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
        if (body is not null) request.Content = new ByteArrayContent(Convert.FromBase64String(body));
        using var response = await client.SendAsync(request);
        return new DAVResponse((int)response.StatusCode, Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync()), response.Headers.ETag?.ToString() ?? "", response.Headers.RetryAfter?.ToString() ?? "");
    }
    public void ExportAttachment(string relative, string destination)
    {
        var source = Resolve(relative);
        destination = Path.GetFullPath(destination);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return;
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try { System.IO.File.Copy(source, temporary); System.IO.File.Move(temporary, destination, true); }
        finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }
    public void Open(string path) => Process.Start(new ProcessStartInfo(Resolve(path)) { UseShellExecute = true });
    public void OpenExternal(string url) { if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) throw new IOException("链接协议不受支持"); Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
    public async Task MigrateAsync(string destination)
    {
        await gate.WaitAsync();
        try
        {
            destination = Path.GetFullPath(destination);
            if (destination.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(Root).StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("新目录不能位于原笔记库内部或包含原笔记库");
            if (Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("请选择一个空文件夹，避免覆盖文件");
            var scan = new Queue<string>(); scan.Enqueue(Root);
            var files = new List<string>();
            while (scan.TryDequeue(out var folder)) foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    if ((System.IO.File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("迁移不支持符号链接或目录联接");
                    if (Directory.Exists(entry)) scan.Enqueue(entry); else files.Add(entry);
                }
            foreach (var path in files)
            {
                var relative = Path.GetRelativePath(Root, path).Replace('\\', '/'); Resolve(relative);
                var target = Path.Combine(destination, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                System.IO.File.Copy(path, target, false);
                var original = SHA256.HashData(await System.IO.File.ReadAllBytesAsync(path)); var copy = SHA256.HashData(await System.IO.File.ReadAllBytesAsync(target));
                if (!original.SequenceEqual(copy)) throw new IOException("迁移校验失败，原目录已保留");
            }
            preferences.Root = destination; SavePreferences();
        }
        finally { gate.Release(); }
    }
    private sealed class NotebookPreferences
    {
        public string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AssistantNotebook");
        public string Url { get; set; } = "";
        public string User { get; set; } = "";
        public string Secret { get; set; } = "";
        public bool Enabled { get; set; }
        public string? Theme { get; set; }
    }
    private sealed record DAVResponse(int status, string data, string etag, string retryAfter);
}
