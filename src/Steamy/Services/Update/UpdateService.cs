using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;

namespace Steamy.Services;

public sealed record UpdateInfo(Version Version, string Tag, Uri AssetUrl, long AssetSize, string? Sha256 = null);

public enum UpdateStage { Downloading, Installing }

public sealed record UpdateProgress(UpdateStage Stage, double Percent, long Bytes, long TotalBytes, double BytesPerSecond);

public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface IUpdateService
{
    Version CurrentVersion { get; }

    /// <summary>The newer release, or null when this build is current. Throws <see cref="UpdateException"/> when GitHub cannot be asked.</summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads the release and swaps the application files. Nothing is changed when it fails.</summary>
    Task InstallAsync(UpdateInfo update, IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Starts the freshly installed executable; the caller then shuts the app down.</summary>
    void StartInstalledVersion();

    /// <summary>Removes the files the previous update moved aside.</summary>
    void CleanUpPreviousInstall();
}

/// <summary>
/// Updates the app from the repository's GitHub releases. Running files cannot be overwritten on
/// Windows but they can be renamed, so every file that is replaced is first moved aside with a
/// ".steamy-old" suffix and the new one is copied in its place. If anything fails, the moved files
/// are put back. The leftovers are deleted on the next start.
/// </summary>
public sealed class GitHubUpdateService : IUpdateService, IDisposable
{
    public const string Repository = "ZazaJr24/Steamy";
    private const string BackupSuffix = ".steamy-old";
    private const string ExecutableName = "Steamy.exe";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _installDirectory;
    private readonly string _workDirectory;
    private readonly Uri _latestReleaseUri;
    public static readonly Uri PublishedManifestUri = new($"https://raw.githubusercontent.com/{Repository}/main/updates/latest.json");

    public GitHubUpdateService(
        HttpClient? http = null,
        string? installDirectory = null,
        string? workDirectory = null,
        Uri? latestReleaseUri = null,
        Version? currentVersion = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(StableDnsHandler.Create()) { Timeout = TimeSpan.FromMinutes(30) };
        _installDirectory = installDirectory ?? AppContext.BaseDirectory;
        _workDirectory = workDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy", "updates");
        _latestReleaseUri = latestReleaseUri ?? new Uri($"https://api.github.com/repos/{Repository}/releases/latest");
        CurrentVersion = Normalize(currentVersion ?? Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));
    }

    public Version CurrentVersion { get; }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _latestReleaseUri);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd($"Steamy/{CurrentVersion}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var document = await ReadDescriptionAsync(response.Content, timeout.Token).ConfigureAwait(false);
                return ParseRelease(document.RootElement, CurrentVersion);
            }
            if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                && (int)response.StatusCode < 500)
                throw new UpdateException($"GitHub answered with HTTP {(int)response.StatusCode}. Download the latest version from github.com/{Repository}/releases/latest.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException
            or KeyNotFoundException or InvalidOperationException or FormatException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // API outages, invalid responses and timeouts can still use the published release manifest.
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, PublishedManifestUri);
            request.Headers.UserAgent.ParseAdd($"Steamy/{CurrentVersion}");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = await ReadDescriptionAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return ParsePublishedManifest(document.RootElement, CurrentVersion);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException
            or KeyNotFoundException or InvalidOperationException or FormatException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new UpdateException($"Both update sources are unavailable. Try again or download the latest version from github.com/{Repository}/releases/latest.", exception);
        }
    }

    private static async Task<JsonDocument> ReadDescriptionAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > 256 * 1024) throw new InvalidDataException("Update description is too large.");
            buffer.Write(block, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    public static UpdateInfo? ParsePublishedManifest(JsonElement manifest, Version currentVersion)
    {
        if (manifest.GetProperty("draft").ValueKind != JsonValueKind.False
            || manifest.GetProperty("prerelease").ValueKind != JsonValueKind.False
            || !manifest.TryGetProperty("published_at", out var published) || !published.TryGetDateTimeOffset(out _))
            throw new InvalidDataException("The update manifest is not a published regular release.");
        var tag = manifest.GetProperty("tag_name").GetString() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$")
            || !Version.TryParse(tag[1..], out var version))
            throw new InvalidDataException("The update manifest has an invalid version.");
        var assets = manifest.GetProperty("assets");
        if (assets.GetArrayLength() != 1) throw new InvalidDataException("The update manifest must identify one app ZIP.");
        var asset = assets[0];
        if (asset.GetProperty("name").GetString() != $"Steamy-{tag}.zip")
            throw new InvalidDataException("The app ZIP does not match the release.");
        var update = ReadAsset(asset, Normalize(version), tag);
        if (update.AssetUrl.AbsoluteUri != $"https://github.com/{Repository}/releases/download/{tag}/Steamy-{tag}.zip"
            || update.AssetSize <= 0 || update.Sha256 is null)
            throw new InvalidDataException("The update manifest has no verified official app ZIP.");
        return update.Version > Normalize(currentVersion) ? update : null;
    }

    public static UpdateInfo? ParseRelease(JsonElement release, Version currentVersion)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) return null;
        var version = Normalize(parsed);
        if (version <= Normalize(currentVersion)) return null;

        JsonElement? legacyAsset = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (name.Equals($"Steamy-{tag}.zip", StringComparison.OrdinalIgnoreCase))
                return ReadAsset(asset, version, tag);
            if (name.Equals("Steamy-latest.zip", StringComparison.OrdinalIgnoreCase)) legacyAsset = asset;
        }
        return legacyAsset is { } fallback ? ReadAsset(fallback, version, tag) : null;
    }

    private static UpdateInfo ReadAsset(JsonElement asset, Version version, string tag)
    {
        string? sha256 = null;
        if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String)
        {
            var value = digest.GetString() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^sha256:[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("The update checksum is invalid.");
            sha256 = value[7..];
        }
        return new(version, tag, new Uri(asset.GetProperty("browser_download_url").GetString()!),
            asset.GetProperty("size").GetInt64(), sha256);
    }

    public async Task InstallAsync(UpdateInfo update, IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureWritable(_installDirectory);
        Directory.CreateDirectory(_workDirectory);
        var archive = Path.Combine(_workDirectory, $"Steamy-{update.Tag}.zip");
        var staging = Path.Combine(_workDirectory, "staging");

        try
        {
            await DownloadAsync(update, archive, progress, cancellationToken).ConfigureAwait(false);

            if (update.Sha256 is { } expected)
            {
                await using var stream = File.OpenRead(archive);
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!digest.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException("The update checksum does not match. Nothing was installed; please try again.");
            }
            progress?.Report(new UpdateProgress(UpdateStage.Installing, 0, 0, 0, 0));
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            try
            {
                ZipFile.ExtractToDirectory(archive, staging);
            }
            catch (InvalidDataException exception)
            {
                throw new UpdateException("The downloaded update is damaged. Please try again.", exception);
            }

            if (!File.Exists(Path.Combine(staging, ExecutableName)))
                throw new UpdateException($"The update does not contain {ExecutableName}.");

            cancellationToken.ThrowIfCancellationRequested();
            ReplaceFiles(staging, _installDirectory, progress);
        }
        finally
        {
            TryDelete(archive);
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task DownloadAsync(UpdateInfo update, string destination, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, update.AssetUrl);
            request.Headers.UserAgent.ParseAdd($"Steamy/{CurrentVersion}");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"The download failed with HTTP {(int)response.StatusCode}.");

            var total = response.Content.Headers.ContentLength ?? update.AssetSize;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                var received = 0L;
                var clock = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                var lastBytes = 0L;
                var speed = 0.0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;

                    var elapsed = clock.Elapsed - lastReport;
                    if (elapsed.TotalMilliseconds < 100 && received != total) continue;
                    var instant = (received - lastBytes) / Math.Max(elapsed.TotalSeconds, 0.001);
                    speed = speed == 0 ? instant : speed + (instant - speed) * 0.3;
                    lastReport = clock.Elapsed;
                    lastBytes = received;
                    progress?.Report(new UpdateProgress(UpdateStage.Downloading, total > 0 ? received * 100.0 / total : 0, received, total, speed));
                }

                if (update.AssetSize > 0 && received != update.AssetSize)
                    throw new UpdateException("The download was incomplete. Please try again.");
            }
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateException("The download was interrupted. Please check your connection and try again.", exception);
        }
    }

    private static void ReplaceFiles(string source, string destination, IProgress<UpdateProgress>? progress)
    {
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        var movedAside = new List<(string Target, string Backup)>();
        var copied = new List<string>();

        try
        {
            for (var index = 0; index < files.Length; index++)
            {
                var relative = Path.GetRelativePath(source, files[index]);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (File.Exists(target))
                {
                    var backup = FreeBackupPath(target);
                    File.Move(target, backup);
                    movedAside.Add((target, backup));
                }

                File.Copy(files[index], target);
                copied.Add(target);
                progress?.Report(new UpdateProgress(UpdateStage.Installing, (index + 1) * 100.0 / files.Length, index + 1, files.Length, 0));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            foreach (var file in copied) TryDelete(file);
            for (var index = movedAside.Count - 1; index >= 0; index--)
            {
                try { File.Move(movedAside[index].Backup, movedAside[index].Target, overwrite: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            throw new UpdateException("A file of Steamy is in use or protected, so nothing was changed. Close other tools started from Steamy and try again.", exception);
        }
    }

    private static string FreeBackupPath(string target)
    {
        var backup = target + BackupSuffix;
        TryDelete(backup);
        return File.Exists(backup) ? $"{target}.{Guid.NewGuid():N}{BackupSuffix}" : backup;
    }

    private static void EnsureWritable(string directory)
    {
        var probe = Path.Combine(directory, $".steamy-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException("Steamy is in a folder it may not write to, so it cannot update itself. Download the new version from the release page instead.", exception);
        }
    }

    public void StartInstalledVersion()
    {
        var executable = Path.Combine(_installDirectory, ExecutableName);
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = _installDirectory });
    }

    public void CleanUpPreviousInstall()
    {
        try
        {
            if (!Directory.Exists(_installDirectory)) return;
            foreach (var leftover in Directory.EnumerateFiles(_installDirectory, "*" + BackupSuffix, SearchOption.AllDirectories))
                TryDelete(leftover);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static Version Normalize(Version version) => version.Revision > 0
        ? new(Math.Max(version.Major, 0), Math.Max(version.Minor, 0), Math.Max(version.Build, 0), version.Revision)
        : new(Math.Max(version.Major, 0), Math.Max(version.Minor, 0), Math.Max(version.Build, 0));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
