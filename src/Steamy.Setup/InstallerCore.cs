using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Steamy.Setup
{
    public sealed class Package
    {
        public string Version, Url, Hash;
        public long Size;
        public bool NeedsRuntime;
        public bool Embedded;
    }
    public sealed class InstallProgress
    {
        public string Title, Detail;
        public double Percent;
        public long Bytes, Total;
        public InstallProgress(string title, string detail, double percent, long bytes = 0, long total = 0)
        { Title = title; Detail = detail; Percent = percent; Bytes = bytes; Total = total; }
    }
    public sealed class InstallerCore : IDisposable
    {
        public const string Repository = "ZazaJr24/Steamy";
        public const string Receipt = ".steamy-install.json";
        readonly HttpClient http;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 100 };
        public bool Finishing { get; private set; }
        public string SelectedVersion { get; private set; }
        public InstallerCore(HttpMessageHandler handler = null)
        {
            http = handler == null ? new HttpClient() : new HttpClient(handler); http.Timeout = TimeSpan.FromMinutes(30);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy-Setup/" + SetupBuild.Version);
        }
        public void Dispose() { http.Dispose(); }
        async Task<Dictionary<string, object>> ReadJson(string url, CancellationToken token)
        {
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                stop.CancelAfter(TimeSpan.FromSeconds(25));
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stop.Token))
                {
                    response.EnsureSuccessStatusCode();
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[16384]; int count;
                        while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, stop.Token)) > 0)
                        {
                            if (output.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("Metadata is too large.");
                            output.Write(buffer, 0, count);
                        }
                        return json.Deserialize<Dictionary<string, object>>(System.Text.Encoding.UTF8.GetString(output.ToArray()));
                    }
                }
            }
        }
        static string Text(Dictionary<string, object> obj, string name) { object value; return obj.TryGetValue(name, out value) ? Convert.ToString(value) : ""; }
        public static Package ParseRelease(Dictionary<string, object> release)
        {
            var tag = Text(release, "tag_name");
            if (!System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+(\.\d+)?$")) throw new InvalidDataException("Invalid release version.");
            if (Text(release, "draft").Equals("True", StringComparison.OrdinalIgnoreCase) || Text(release, "prerelease").Equals("True", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A stable release is required.");
            object raw; if (!release.TryGetValue("assets", out raw)) throw new InvalidDataException("No app package was published.");
            var assets = ((IEnumerable)raw).Cast<Dictionary<string, object>>().ToArray();
            var smallName = "Steamy-" + tag + "-app.zip";
            var asset = assets.SingleOrDefault(a => Text(a, "name") == smallName)
                ?? assets.SingleOrDefault(a => Text(a, "name") == "Steamy-" + tag + ".zip");
            if (asset == null) throw new InvalidDataException("The release does not contain the Steamy app ZIP.");
            var url = Text(asset, "browser_download_url");
            if (url != "https://github.com/" + Repository + "/releases/download/" + tag + "/" + Text(asset, "name")) throw new InvalidDataException("Unexpected app download location.");
            var digest = Text(asset, "digest");
            if (!System.Text.RegularExpressions.Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException("This release has no verified SHA-256 digest. Publish the verified app package first.");
            long size; if (!long.TryParse(Text(asset, "size"), out size) || size <= 0 || size > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Invalid app package size.");
            return new Package { Version = tag, Url = url, Hash = digest.Substring(7), Size = size, NeedsRuntime = Text(asset, "name") == smallName };
        }
        public async Task<Package> GetPackage(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var embedded = EmbeddedPayload.GetPackage();
            if (embedded != null) return embedded;
            try { return RequireBuildVersion(ParseRelease(await ReadJson("https://api.github.com/repos/" + Repository + "/releases/tags/v" + SetupBuild.Version, token))); }
            catch (Exception e)
            {
                if (!(e is HttpRequestException || e is TaskCanceledException || e is InvalidDataException)) throw;
                token.ThrowIfCancellationRequested();
            }
            try { return RequireBuildVersion(ParseRelease(await ReadJson("https://raw.githubusercontent.com/" + Repository + "/main/updates/installer.json", token))); }
            catch (Exception e) { if (!(e is HttpRequestException || e is TaskCanceledException || e is InvalidDataException)) throw; token.ThrowIfCancellationRequested(); }
            return RequireBuildVersion(ParseRelease(await ReadJson("https://raw.githubusercontent.com/" + Repository + "/main/updates/latest.json", token)));
        }
        internal static Package RequireBuildVersion(Package package)
        {
            if (package.Version != "v" + SetupBuild.Version) throw new InvalidDataException("The app package for Steamy " + SetupBuild.Version + " has not been published. Use the complete setup or portable package for this version.");
            return package;
        }
        async Task Download(string url, string path, long expectedSize, string digest, bool sha512,
            Action<long, long> report, CancellationToken token)
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage != null && response.RequestMessage.RequestUri.Scheme != "https") throw new InvalidDataException("An encrypted download connection is required.");
                var total = expectedSize > 0 ? expectedSize : response.Content.Headers.ContentLength.GetValueOrDefault();
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    byte[] buffer = new byte[65536]; long bytes = 0; int count;
                    var watch = Stopwatch.StartNew();
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                    {
                        bytes += count;
                        if (bytes > 2L * 1024 * 1024 * 1024 || total > 0 && bytes > total) throw new InvalidDataException("Unexpected download size.");
                        await output.WriteAsync(buffer, 0, count, token);
                        if (watch.ElapsedMilliseconds > 80) { report(bytes, total); watch.Restart(); }
                    }
                    if (total > 0 && bytes != total) throw new InvalidDataException("The download is incomplete. Retry to download it again.");
                    report(bytes, total);
                }
            }
            token.ThrowIfCancellationRequested();
            if (digest != null) VerifyHash(path, digest, sha512);
        }
        internal Task CheckDownload(string url, string path, long size, string hash, CancellationToken token)
        { return Download(url, path, size, hash, false, (a, b) => { }, token); }
        public static void VerifyHash(string path, string expected, bool sha512)
        {
            using (var stream = File.OpenRead(path))
            using (HashAlgorithm algorithm = sha512 ? (HashAlgorithm)SHA512.Create() : SHA256.Create())
            {
                var actual = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "");
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Download verification failed. No downloaded program was run.");
            }
        }
        public static bool CompatibleRuntime(string value)
        {
            Version version;
            return Version.TryParse(value, out version) && version.Major == 9 && version.Minor == 0 && version >= new Version(9, 0, 20);
        }
        public static bool RuntimePresent()
        {
            var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared");
            foreach (var component in new[] { "Microsoft.WindowsDesktop.App", "Microsoft.NETCore.App" })
            {
                var folder = Path.Combine(shared, component);
                var file = component == "Microsoft.WindowsDesktop.App" ? "PresentationFramework.dll" : "coreclr.dll";
                if (!Directory.Exists(folder) || !Directory.GetDirectories(folder).Any(path => CompatibleRuntime(Path.GetFileName(path)) && File.Exists(Path.Combine(path, file)))) return false;
            }
            return true;
        }
        async Task EnsureRuntime(string work, IProgress<InstallProgress> progress, CancellationToken token)
        {
            if (RuntimePresent()) return;
            progress.Report(new InstallProgress("Preparing your runtime", "Getting the Windows Desktop runtime from Microsoft.", 4));
            var releases = await ReadJson("https://builds.dotnet.microsoft.com/dotnet/release-metadata/9.0/releases.json", token);
            var latest = Text(releases, "latest-runtime");
            var release = ((IEnumerable)releases["releases"]).Cast<Dictionary<string, object>>()
                .First(item => Text(item, "release-version") == latest);
            var desktop = (Dictionary<string, object>)release["windowsdesktop"];
            var file = ((IEnumerable)desktop["files"]).Cast<Dictionary<string, object>>()
                .Single(item => Text(item, "name") == "windowsdesktop-runtime-win-x64.exe");
            var url = new Uri(Text(file, "url")); var hash = Text(file, "hash");
            if (url.Scheme != "https" || url.Host != "builds.dotnet.microsoft.com" || !System.Text.RegularExpressions.Regex.IsMatch(hash, @"^[a-fA-F0-9]{128}$")) throw new InvalidDataException("Invalid Microsoft runtime metadata.");
            var runtime = Path.Combine(work, "windowsdesktop-runtime.exe");
            await Download(url.AbsoluteUri, runtime, 0, hash, true, (bytes, total) => progress.Report(new InstallProgress("Downloading the runtime", "Microsoft .NET Windows Desktop " + latest, total > 0 ? 5 + 20d * bytes / total : 5, bytes, total)), token);
            token.ThrowIfCancellationRequested(); Finishing = true;
            try
            {
                progress.Report(new InstallProgress("Installing the runtime", "Windows may ask you to approve Microsoft's installer.", 26));
                using (var process = Process.Start(new ProcessStartInfo(runtime, "/install /passive /norestart") { UseShellExecute = true, Verb = "runas" }))
                {
                    await Task.Run(() => process.WaitForExit());
                    if (process.ExitCode != 0 && process.ExitCode != 3010) throw new IOException("Microsoft's runtime installation did not complete (" + process.ExitCode + "). Retry when ready.");
                }
                if (!RuntimePresent()) throw new IOException("The Windows Desktop runtime is not available yet. Restart Windows if requested, then retry.");
            }
            finally { Finishing = false; }
        }
        public static bool WebViewPresent()
        {
            foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry32, Microsoft.Win32.RegistryView.Registry64 })
            using (var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view))
            using (var key = root.OpenSubKey(@"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"))
            {
                Version version;
                if (key != null && Version.TryParse(Convert.ToString(key.GetValue("pv")), out version) && version > new Version(0, 0, 0, 0)) return true;
            }
            return false;
        }
        public static void VerifyMicrosoftPublisher(string path)
        {
            // Evergreen's bootstrapper changes over time; require a valid Microsoft Authenticode signature.
            var command = "$s=Get-AuthenticodeSignature -LiteralPath '" + path.Replace("'", "''") + "'; if($s.Status -eq 'Valid' -and $s.SignerCertificate.Subject -match '(^|, )O=Microsoft Corporation(,|$)'){exit 0}else{exit 1}";
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            using (var process = Process.Start(new ProcessStartInfo(powershell, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command))) { UseShellExecute = false, CreateNoWindow = true }))
            {
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidDataException("Microsoft's browser component could not be verified. No downloaded program was run.");
            }
        }
        async Task EnsureWebView(string work, IProgress<InstallProgress> progress, CancellationToken token)
        {
            if (WebViewPresent()) return;
            var file = Path.Combine(work, "MicrosoftEdgeWebview2Setup.exe");
            await Download("https://go.microsoft.com/fwlink/p/?LinkId=2124703", file, 0, null, false,
                (bytes, total) => progress.Report(new InstallProgress("Preparing browser components", "Downloading Microsoft's WebView2 installer.", 28, bytes, total)), token);
            await Task.Run(() => VerifyMicrosoftPublisher(file), token);
            token.ThrowIfCancellationRequested(); Finishing = true;
            try
            {
                progress.Report(new InstallProgress("Installing browser components", "Microsoft WebView2 · only when missing.", 29));
                using (var process = Process.Start(new ProcessStartInfo(file, "/silent /install") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    await Task.Run(() => process.WaitForExit());
                    if (process.ExitCode != 0 && process.ExitCode != 3010) throw new IOException("Microsoft's browser component installation did not complete (" + process.ExitCode + ").");
                }
                if (!WebViewPresent()) throw new IOException("WebView2 is not available yet. Restart Windows if requested, then retry.");
            }
            finally { Finishing = false; }
        }
        public static string WorkRoot()
        {
            var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steamy");
            var active = Environment.GetEnvironmentVariable("STEAMY_DATA_DIR");
            var locator = Path.Combine(roaming, "storage-location.json");
            if (string.IsNullOrWhiteSpace(active) && File.Exists(locator))
            {
                RejectLinks(locator);
                if (new FileInfo(locator).Length > 65536) throw new InvalidDataException("Storage location metadata is too large.");
                var location = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(locator));
                active = Text(location, "ActiveRoot");
            }
            if (string.IsNullOrWhiteSpace(active)) active = roaming;
            if (!FullyQualified(active)) throw new IOException("Your Steamy data folder must be an absolute path.");
            RejectLinks(active);
            return Path.Combine(active, "installer");
        }
        static bool FullyQualified(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && (path.StartsWith(@"\\") || path.Length > 2 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));
        }
        public async Task Install(string target, bool desktopShortcut, IProgress<InstallProgress> progress, CancellationToken token)
        {
            ValidateTarget(target);
            var work = Path.Combine(WorkRoot(), Guid.NewGuid().ToString("N"));
            RejectLinks(work); Directory.CreateDirectory(work);
            try
            {
                progress.Report(new InstallProgress("Preparing Steamy " + SetupBuild.Version, "Checking the app package.", 1));
                var package = await GetPackage(token); SelectedVersion = package.Version;
                if (package.NeedsRuntime) await EnsureRuntime(work, progress, token);
                await EnsureWebView(work, progress, token);
                var archive = Path.Combine(work, "app.zip");
                if (package.Embedded) await EmbeddedPayload.WriteArchive(archive, package, progress, token);
                else await Download(package.Url, archive, package.Size, package.Hash, false,
                    (bytes, total) => progress.Report(new InstallProgress("Downloading Steamy", package.Version + " · app and components", 30 + 55d * bytes / total, bytes, total)), token);
                var stage = Path.Combine(work, "app");
                progress.Report(new InstallProgress("Putting everything together", "Verifying and preparing your files.", 88));
                await Task.Run(() => Extract(archive, stage, token, package.NeedsRuntime), token);
                await PrepareTools(stage, work, progress, token);
                token.ThrowIfCancellationRequested(); Finishing = true;
                try
                {
                    progress.Report(new InstallProgress("Almost ready", "Installing Steamy in your chosen folder.", 96));
                    Commit(stage, target, package.Version);
                    CreateShortcut(target, Environment.GetFolderPath(Environment.SpecialFolder.Programs));
                    if (desktopShortcut) CreateShortcut(target, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                }
                finally { Finishing = false; }
                progress.Report(new InstallProgress("Your library is ready", "Steamy is installed. Your games and settings stay yours.", 100));
            }
            finally { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        static async Task PrepareTools(string stage, string work, IProgress<InstallProgress> progress, CancellationToken token)
        {
            var capability = Path.Combine(stage, "tool-preparation-capabilities.json");
            if (!File.Exists(capability)) throw new IOException("This published app package does not support setup tool preparation yet. Use the matching new app release or its portable package.");
            var statusFile = Path.Combine(work, "tool-preparation-status.json");
            progress.Report(new InstallProgress("Preparing your tools", "Downloading and verifying official tool packages.", 89));
            var info = new ProcessStartInfo(Path.Combine(stage, "Steamy.exe"), "--prepare-tools \"" + statusFile + "\"")
            { WorkingDirectory = stage, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (var process = Process.Start(info))
            {
                if (process == null) throw new IOException("Tool preparation could not start.");
                var started = DateTime.UtcNow;
                try
                {
                    while (!process.HasExited)
                    {
                        token.ThrowIfCancellationRequested();
                        if (DateTime.UtcNow - started > TimeSpan.FromMinutes(20)) throw new IOException("Tool preparation timed out. Retry setup to continue from the cache.");
                        if (File.Exists(statusFile))
                        {
                            try
                            {
                                var status = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(statusFile));
                                var completed = Convert.ToDouble(status["Completed"]); var total = Convert.ToDouble(status["Total"]);
                                progress.Report(new InstallProgress("Preparing your tools", Text(status, "Phase"), 89 + 6 * completed / Math.Max(1, total)));
                            }
                            catch (IOException) { }
                        }
                        await Task.Delay(300, token);
                    }
                    if (process.ExitCode != 0)
                    {
                        var status = File.Exists(statusFile) ? new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(statusFile)) : null;
                        throw new IOException(status == null ? "Tool preparation failed. Retry setup." : Text(status, "Error"));
                    }
                }
                finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
            }
        }
        public static void RejectLinks(string path)
        {
            for (var entry = new DirectoryInfo(Path.GetFullPath(path)); entry != null; entry = entry.Parent)
                if ((Directory.Exists(entry.FullName) || File.Exists(entry.FullName)) && (File.GetAttributes(entry.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Choose a regular folder, without symbolic links or junctions.");
        }
        public static void ValidateTarget(string target)
        {
            var full = Path.GetFullPath(target); RejectLinks(full);
            if (!FullyQualified(target) || full.TrimEnd('\\') == Path.GetPathRoot(full).TrimEnd('\\') || File.Exists(full)) throw new IOException("Choose a separate application folder.");
            if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !File.Exists(Path.Combine(full, Receipt)))
                throw new IOException("This folder contains other files. Choose an empty folder or a previous Steamy installer folder.");
        }
        public static void Extract(string archive, string target, CancellationToken token, bool requiresRuntimeConfig = true)
        {
            RejectLinks(target); Directory.CreateDirectory(target);
            var root = Path.GetFullPath(target).TrimEnd('\\') + "\\";
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count > 10000) throw new InvalidDataException("Too many app files.");
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('/', '\\');
                    if (name.StartsWith("\\") || name.Contains(":") || name.Split('\\').Any(part => part == "..") || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("Invalid path inside the app archive.");
                    var path = Path.GetFullPath(Path.Combine(target, name));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !names.Add(path)) throw new InvalidDataException("Unsafe or duplicate archive path.");
                    total += entry.Length;
                    if (total > 2L * 1024 * 1024 * 1024 || entry.Length > 1024L * 1024 * 1024) throw new InvalidDataException("The app archive is too large.");
                    if (name.EndsWith("\\")) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)); entry.ExtractToFile(path);
                }
            }
            foreach (var name in requiresRuntimeConfig ? new[] { "Steamy.exe", "PlayTimeTracker.exe", "Steamy.runtimeconfig.json", "PlayTimeTracker.runtimeconfig.json" } : new[] { "Steamy.exe", "PlayTimeTracker.exe" })
                if (!File.Exists(Path.Combine(target, name))) throw new InvalidDataException("The app archive is missing " + name + ".");
        }
        public static void Commit(string stage, string target, string version, bool checkRunning = true)
        {
            ValidateTarget(target);
            if (checkRunning && Process.GetProcessesByName("Steamy").Any(process => { using (process) return !process.HasExited; })) throw new IOException("Close your running Steamy before installing, then retry. Its files have not been changed.");
            var next = target.TrimEnd('\\') + ".new-" + Guid.NewGuid().ToString("N");
            var previous = target.TrimEnd('\\') + ".previous-" + Guid.NewGuid().ToString("N");
            bool saved = false, committed = false;
            try
            {
                CopyTree(stage, next);
                File.WriteAllText(Path.Combine(next, Receipt), new JavaScriptSerializer().Serialize(new { version = version, installed = DateTime.UtcNow.ToString("O") }));
                ValidateTarget(target);
                if (Directory.Exists(target)) { Directory.Move(target, previous); saved = true; }
                try { Directory.Move(next, target); committed = true; }
                catch { if (saved && !Directory.Exists(target)) Directory.Move(previous, target); throw; }
            }
            finally { if (!committed && Directory.Exists(next)) Directory.Delete(next, true); }
            // The previous application remains as a recoverable copy. User data is never deleted.
        }
        static void CopyTree(string source, string target)
        {
            RejectLinks(source); RejectLinks(target); Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source)) { RejectLinks(file); File.Copy(file, Path.Combine(target, Path.GetFileName(file))); }
            foreach (var folder in Directory.GetDirectories(source)) CopyTree(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
        static void CreateShortcut(string target, string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic link = shell.CreateShortcut(Path.Combine(folder, "Steamy.lnk"));
                link.TargetPath = Path.Combine(target, "Steamy.exe"); link.WorkingDirectory = target;
                link.IconLocation = Path.Combine(target, "Steamy.exe") + ",0"; link.Save();
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
            catch (Exception) { } // App installation still succeeds if shortcuts are disabled.
        }
    }
}
