using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Timetable.Updates;

namespace Timetable.Desktop;

internal sealed record AvailableUpdate(string Version, string Notes, Uri Download, string Digest, long Size);
internal sealed record UpdateCheck(string Current, string? Remote, AvailableUpdate? Update);

internal sealed class UpdateClient : IDisposable
{
    internal const string AssetName = "DesktopTimetable-update-win-x64.zip";
    private const string Repository = "FylzX/timetable-for-seewo";
    private readonly HttpClient http;
    private readonly Uri? testBase;
    private const long MaximumPackage = 1024L * 1024 * 1024;
    internal static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version is { } version
        ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.4.0";

    internal UpdateClient(Uri? testBase = null, TimeSpan? timeout = null)
    {
        if (testBase != null && (testBase.Scheme != "http" || !testBase.IsLoopback || testBase.AbsolutePath != "/" || testBase.Query != ""))
            throw new InvalidDataException("模拟更新服务只能使用本机 HTTP 地址。");
        this.testBase = testBase;
        http = new HttpClient { Timeout = timeout ?? TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopTimetable/" + CurrentVersion);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    internal async Task<UpdateCheck> CheckAsync(string current, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var uri = testBase == null ? new Uri($"https://api.github.com/repos/{Repository}/releases/latest")
            : new Uri(testBase, $"repos/{Repository}/releases/latest");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(current, null, null);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadLimitedAsync(response, 1024 * 1024, deadline.Token);
        using var json = JsonDocument.Parse(bytes);
        var release = json.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("远程版本不是正式发布版。");
        var remote = (release.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        var remoteVersion = ParseVersion(remote);
        if (remoteVersion <= ParseVersion(current)) return new(current, remote, null);
        var matches = release.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == AssetName).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"发现 v{remote}，但 Release 缺少唯一的 {AssetName} 更新附件。");
        var asset = matches[0];
        var download = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        ValidateDownloadUri(download);
        var size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaximumPackage) throw new InvalidDataException("更新包大小无效。");
        string digest;
        if (asset.TryGetProperty("digest", out var digestValue) && digestValue.GetString() is { } supplied
            && supplied.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) digest = supplied[7..];
        else
        {
            var checksum = release.GetProperty("assets").EnumerateArray()
                .SingleOrDefault(item => item.GetProperty("name").GetString() == AssetName + ".sha256");
            if (checksum.ValueKind != JsonValueKind.Object) throw new InvalidDataException("更新附件缺少 SHA-256 校验信息。");
            var checksumUri = new Uri(checksum.GetProperty("browser_download_url").GetString()!);
            ValidateDownloadUri(checksumUri);
            using var checksumResponse = await http.GetAsync(checksumUri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            checksumResponse.EnsureSuccessStatusCode();
            digest = System.Text.Encoding.UTF8.GetString(await ReadLimitedAsync(checksumResponse, 4096, deadline.Token)).Trim().Split(' ', '\t', '\r', '\n')[0];
        }
        if (!Regex.IsMatch(digest, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("更新附件的 SHA-256 校验信息无效。");
        return new(current, remote, new AvailableUpdate(remote, release.GetProperty("body").GetString() ?? "", download, digest, size));
    }

    internal async Task<string> DownloadAsync(AvailableUpdate update, string installRoot, IProgress<double>? progress, CancellationToken token)
    {
        ValidateDownloadUri(update.Download);
        UpdatePackage.CheckLinks(installRoot);
        var work = Path.Combine(installRoot, ".update");
        Directory.CreateDirectory(work); UpdatePackage.CheckLinks(work);
        var job = Path.Combine(work, "download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        var archive = Path.Combine(job, "package.zip");
        var stage = Path.Combine(job, "stage");
        try
        {
            using (var response = await http.GetAsync(update.Download, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920]; long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    received += count;
                    if (received > update.Size || received > MaximumPackage) throw new InvalidDataException("下载大小与发布信息不匹配。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    progress?.Report((double)received / update.Size * 100);
                }
                if (received != update.Size) throw new InvalidDataException("更新包下载不完整。");
            }
            if (!String.Equals(UpdatePackage.Hash(archive), update.Digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包 SHA-256 校验失败，已停止更新。");
            Directory.CreateDirectory(stage);
            using (var zip = ZipFile.OpenRead(archive))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.FullName.EndsWith('/')) continue;
                    if ((entry.FullName != "update-manifest.xml" && !UpdatePackage.SafePath(entry.FullName))
                        || !names.Add(entry.FullName) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                        throw new InvalidDataException("更新包包含不允许的路径、链接或重复文件。");
                    expanded += entry.Length;
                    if (expanded > 2 * MaximumPackage) throw new InvalidDataException("解压后更新包过大。");
                    var destination = Path.Combine(stage, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination);
                }
            }
            await Task.Run(() => UpdatePackage.Validate(stage, update.Version), token);
            File.Delete(archive);
            return stage;
        }
        catch { Directory.Delete(job, true); throw; }
    }

    internal static Version ParseVersion(string text)
    {
        if (!Regex.IsMatch(text, "^[0-9]+\\.[0-9]+\\.[0-9]+$") || !Version.TryParse(text, out var version))
            throw new InvalidDataException("无法识别版本号：" + text);
        return version;
    }

    private void ValidateDownloadUri(Uri uri)
    {
        if (testBase != null)
        {
            if (uri.Scheme != "http" || uri.Host != testBase.Host || uri.Port != testBase.Port || !uri.IsLoopback)
                throw new InvalidDataException("模拟服务返回了非本机下载地址。");
        }
        else if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("更新附件不属于指定 GitHub 仓库。");
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("服务器响应过大。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    public void Dispose() => http.Dispose();
}
