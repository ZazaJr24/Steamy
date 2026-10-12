using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Steamy.Setup
{
    static class InstallerChecks
    {
        static int count;
        static void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        static void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { count++; return; } catch (IOException) { count++; return; } throw new Exception(message); }
        public static int Run(string report)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(report));
            var root = Path.Combine(parent, "installer-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Check(!InstallerCore.CompatibleRuntime("9.0.19"), "Old runtime must be upgraded to the packaged patch minimum.");
                Check(InstallerCore.CompatibleRuntime("9.0.20"), "Current runtime can be reused.");
                Check(InstallerCore.CompatibleRuntime("9.0.21"), "Compatible newer patch can be reused.");
                Check(!InstallerCore.CompatibleRuntime("10.0.0"), "Different runtime major cannot satisfy the app target.");
                var file = Path.Combine(root, "bytes"); var bytes = new byte[] { 1, 2, 3, 4, 5 }; File.WriteAllBytes(file, bytes);
                string hash; using (var algorithm = SHA256.Create()) hash = BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "");
                InstallerCore.VerifyHash(file, hash, false); Check(true, "Valid hash");
                Reject(() => InstallerCore.VerifyHash(file, new string('0', 64), false), "Hash mismatch must fail.");
                Check(InstallerCore.RequireBuildVersion(new Package { Version = "v" + SetupBuild.Version }).Version == "v" + SetupBuild.Version, "Matching setup version is accepted.");
                Reject(() => InstallerCore.RequireBuildVersion(new Package { Version = "v0.0.0" }), "Setup must not install another release.");
                var copy = Path.Combine(root, "embedded-copy.zip");
                var fixturePackage = new Package { Version = "v" + SetupBuild.Version, Size = bytes.Length, Hash = hash, Embedded = true };
                using (var input = new MemoryStream(bytes)) EmbeddedPayload.CopyVerifiedArchive(input, copy, fixturePackage, null, CancellationToken.None).GetAwaiter().GetResult();
                Check(File.ReadAllBytes(copy).Length == bytes.Length, "Embedded payload is copied and verified.");
                using (var input = new MemoryStream(bytes))
                    Reject(() => EmbeddedPayload.CopyVerifiedArchive(input, Path.Combine(root, "embedded-short"), new Package { Size = bytes.Length + 1, Hash = hash }, null, CancellationToken.None).GetAwaiter().GetResult(), "Truncated embedded payload must fail.");
                using (var input = new MemoryStream(bytes))
                    Reject(() => EmbeddedPayload.CopyVerifiedArchive(input, Path.Combine(root, "embedded-long"), new Package { Size = bytes.Length - 1, Hash = hash }, null, CancellationToken.None).GetAwaiter().GetResult(), "Oversized embedded payload must fail.");
                using (var input = new MemoryStream(bytes))
                    Reject(() => EmbeddedPayload.CopyVerifiedArchive(input, Path.Combine(root, "embedded-corrupt"), new Package { Size = bytes.Length, Hash = new string('0', 64) }, null, CancellationToken.None).GetAwaiter().GetResult(), "Corrupt embedded payload must fail.");
                var cancelledCopy = Path.Combine(root, "embedded-cancelled"); bool copyStopped = false;
                using (var input = new MemoryStream(bytes))
                    try { EmbeddedPayload.CopyVerifiedArchive(input, cancelledCopy, fixturePackage, null, new CancellationToken(true)).GetAwaiter().GetResult(); } catch (OperationCanceledException) { copyStopped = true; }
                Check(copyStopped && !File.Exists(cancelledCopy), "Cancelled payload must not create a file.");
                var bundledPackage = EmbeddedPayload.GetPackage();
                if (SetupBuild.PayloadBytes == 0) Check(bundledPackage == null, "Web setup has no embedded app.");
                else
                {
                    Check(bundledPackage != null && bundledPackage.Embedded && !bundledPackage.NeedsRuntime && bundledPackage.Version == "v" + SetupBuild.Version, "Complete setup selects its own portable app.");
                    var bundledArchive = Path.Combine(root, "bundled.zip");
                    EmbeddedPayload.WriteArchive(bundledArchive, bundledPackage, null, CancellationToken.None).GetAwaiter().GetResult();
                    Check(new FileInfo(bundledArchive).Length == SetupBuild.PayloadBytes, "Actual embedded payload passes size and hash verification.");
                    using (var bundled = ZipFile.OpenRead(bundledArchive))
                    {
                        Check(bundled.GetEntry("Steamy.exe") != null && bundled.GetEntry("WindowsBase.dll") != null && bundled.GetEntry("PlayTimeTracker.exe") != null, "Complete setup includes app, tracker and WPF runtime.");
                        foreach (var assemblyName in new[] { "Steamy.dll", "PlayTimeTracker.dll" })
                        {
                            var assemblyPath = Path.Combine(root, "checked-" + assemblyName);
                            bundled.GetEntry(assemblyName).ExtractToFile(assemblyPath);
                            var actualVersion = System.Reflection.AssemblyName.GetAssemblyName(assemblyPath).Version;
                            Check(actualVersion == new Version(SetupBuild.Version + (SetupBuild.Version.Split('.').Length == 3 ? ".0" : "")), "Embedded " + assemblyName + " matches setup version.");
                        }
                    }
                }
                using (var core = new InstallerCore(new BytesHandler(bytes)))
                {
                    core.CheckDownload("https://example.invalid/app", Path.Combine(root, "download"), bytes.Length, hash, CancellationToken.None).GetAwaiter().GetResult();
                    Check(File.ReadAllBytes(Path.Combine(root, "download")).Length == bytes.Length, "Streaming download");
                    Reject(() => core.CheckDownload("https://example.invalid/app", Path.Combine(root, "bad-size"), bytes.Length + 1, hash, CancellationToken.None).GetAwaiter().GetResult(), "Incomplete downloads must fail.");
                    var cancelled = new CancellationToken(true); bool stopped = false;
                    try { core.CheckDownload("https://example.invalid/app", Path.Combine(root, "cancelled"), bytes.Length, hash, cancelled).GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
                    Check(stopped && !File.Exists(Path.Combine(root, "cancelled")), "Cancellation must not commit files.");
                }
                var tag = "v" + SetupBuild.Version;
                var asset = new Dictionary<string, object> { { "name", "Steamy-" + tag + "-app.zip" }, { "browser_download_url", "https://github.com/ZazaJr24/Steamy/releases/download/" + tag + "/Steamy-" + tag + "-app.zip" }, { "size", 100 }, { "digest", "sha256:" + hash } };
                var release = new Dictionary<string, object> { { "tag_name", tag }, { "draft", false }, { "prerelease", false }, { "assets", new object[] { asset } } };
                Check(InstallerCore.ParseRelease(release).NeedsRuntime, "App package requires runtime.");
                asset["browser_download_url"] = "https://other.invalid/app.zip";
                Reject(() => InstallerCore.ParseRelease(release), "Wrong download origin must fail.");
                asset["browser_download_url"] = "https://github.com/ZazaJr24/Steamy/releases/download/" + tag + "/Steamy-" + tag + "-app.zip";
                asset["digest"] = ""; Reject(() => InstallerCore.ParseRelease(release), "Missing hashes must fail.");
                var archive = Path.Combine(root, "good.zip");
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                    foreach (var name in new[] { "Steamy.exe", "PlayTimeTracker.exe", "Steamy.runtimeconfig.json", "PlayTimeTracker.runtimeconfig.json", "Resources/license.txt" })
                        using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("fixture");
                var stage = Path.Combine(root, "stage"); InstallerCore.Extract(archive, stage, CancellationToken.None);
                Check(File.Exists(Path.Combine(stage, "Resources", "license.txt")), "All components extracted.");
                var bad = Path.Combine(root, "escape.zip"); using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create)) zip.CreateEntry("../outside.exe");
                Reject(() => InstallerCore.Extract(bad, Path.Combine(root, "escape"), CancellationToken.None), "Traversal must fail.");
                Check(!File.Exists(Path.Combine(root, "outside.exe")), "No escaped file.");
                var duplicate = Path.Combine(root, "duplicate.zip"); using (var zip = ZipFile.Open(duplicate, ZipArchiveMode.Create)) { zip.CreateEntry("Steamy.exe"); zip.CreateEntry("STEAMY.EXE"); }
                Reject(() => InstallerCore.Extract(duplicate, Path.Combine(root, "duplicate"), CancellationToken.None), "Duplicate file paths must fail.");
                var unrelated = Path.Combine(root, "unrelated"); Directory.CreateDirectory(unrelated); File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "keep");
                Reject(() => InstallerCore.Commit(stage, unrelated, "fixture", false), "Existing unrelated folders must not be overwritten.");
                Check(File.ReadAllText(Path.Combine(unrelated, "keep.txt")) == "keep", "Unrelated data unchanged.");
                Reject(() => InstallerCore.ValidateTarget(@"C:relative"), "Drive-relative target must fail.");
                var unsigned = Path.Combine(root, "unsigned.exe"); File.WriteAllBytes(unsigned, bytes);
                Reject(() => InstallerCore.VerifyMicrosoftPublisher(unsigned), "Unsigned runtime cannot execute.");
                asset["name"] = "Steamy-" + tag + ".zip"; asset["browser_download_url"] = "https://github.com/ZazaJr24/Steamy/releases/download/" + tag + "/Steamy-" + tag + ".zip"; asset["digest"] = "sha256:" + hash;
                Check(!InstallerCore.ParseRelease(release).NeedsRuntime, "Portable release does not require global .NET.");
                var target = Path.Combine(root, "installed"); InstallerCore.Commit(stage, target, "first", false);
                Check(File.Exists(Path.Combine(target, InstallerCore.Receipt)), "Installation committed.");
                File.WriteAllText(Path.Combine(target, "user.txt"), "retained"); InstallerCore.Commit(stage, target, "second", false);
                Check(Directory.GetDirectories(root, "installed.previous-*").Length == 1 && File.Exists(Path.Combine(Directory.GetDirectories(root, "installed.previous-*")[0], "user.txt")), "Previous files preserved on update.");
                File.WriteAllText(report, count + " installer checks passed. No runtime was executed and no real application was installed.");
                return 0;
            }
            catch (Exception error) { File.WriteAllText(report, error.ToString()); return 1; }
            finally
            {
                if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(parent).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
            }
        }
        sealed class BytesHandler : HttpMessageHandler
        {
            readonly byte[] bytes; public BytesHandler(byte[] value) { bytes = value; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            { token.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }
        }
    }
}
