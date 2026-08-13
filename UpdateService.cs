using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanDrop;

public sealed record UpdateRelease(Version Version, string Tag, string PageUrl, string DownloadUrl, string ChecksumUrl);

public static class UpdateService
{
    public const string Repository = "Yumouqianxia/LanDrop";
    private const string ExecutableAsset = "LanDrop.exe";
    private const string ChecksumAsset = "LanDrop.exe.sha256";
    private static readonly HttpClient Client = CreateClient();

    public static Version CurrentVersion
    {
        get
        {
            Version value = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(value.Major, value.Minor, Math.Max(0, value.Build));
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LanDrop-Windows-Updater/2.3");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<UpdateRelease?> CheckAsync(CancellationToken token)
    {
        string api = $"https://api.github.com/repos/{Repository}/releases/latest";
        using HttpResponseMessage response = await Client.GetAsync(api, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using Stream json = await response.Content.ReadAsStreamAsync(token);
        GitHubRelease release = await JsonSerializer.DeserializeAsync<GitHubRelease>(json, cancellationToken: token)
            ?? throw new InvalidDataException("GitHub 返回了无效的版本信息。");
        if (!Version.TryParse(release.TagName.TrimStart('v', 'V'), out Version? version)) return null;
        GitHubAsset? executable = release.Assets.FirstOrDefault(x => x.Name == ExecutableAsset);
        GitHubAsset? checksum = release.Assets.FirstOrDefault(x => x.Name == ChecksumAsset);
        if (executable is null || checksum is null || version <= CurrentVersion) return null;
        return new UpdateRelease(version, release.TagName, release.HtmlUrl, executable.DownloadUrl, checksum.DownloadUrl);
    }

    public static async Task DownloadAndInstallAsync(UpdateRelease release, IProgress<string>? progress, CancellationToken token)
    {
        progress?.Report("正在下载新版…");
        byte[] executable = await Client.GetByteArrayAsync(release.DownloadUrl, token);
        string checksumText = await Client.GetStringAsync(release.ChecksumUrl, token);
        string expected = checksumText.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        string actual = Convert.ToHexString(SHA256.HashData(executable));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("新版文件的 SHA-256 校验失败，已取消更新。");

        string? currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable)) throw new InvalidOperationException("无法确定当前程序路径。");
        string updateDirectory = Path.Combine(Path.GetTempPath(), "LanDropUpdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateDirectory);
        string downloadedExecutable = Path.Combine(updateDirectory, ExecutableAsset);
        string scriptPath = Path.Combine(updateDirectory, "install-update.ps1");
        await File.WriteAllBytesAsync(downloadedExecutable, executable, token);

        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $processId = {{Environment.ProcessId}}
            $current = {{Quote(currentExecutable)}}
            $downloaded = {{Quote(downloadedExecutable)}}
            $backup = $current + '.previous'
            try {
                Wait-Process -Id $processId -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
                Move-Item -LiteralPath $current -Destination $backup -Force
                Move-Item -LiteralPath $downloaded -Destination $current -Force
                Start-Process -FilePath $current
                Start-Sleep -Seconds 2
                if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
            } catch {
                if (-not (Test-Path -LiteralPath $current) -and (Test-Path -LiteralPath $backup)) {
                    Move-Item -LiteralPath $backup -Destination $current -Force
                }
                throw
            } finally {
                Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
            }
            """;
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false), token);
        progress?.Report("校验完成，正在安装…");
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private sealed class GitHubRelease
    {
        [System.Text.Json.Serialization.JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; } = "";
    }
}
