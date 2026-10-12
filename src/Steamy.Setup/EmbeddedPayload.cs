using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Steamy.Setup
{
    internal static class EmbeddedPayload
    {
        const string ResourceName = "Steamy.Setup.Payload.zip";
        internal static Package GetPackage()
        {
            if (SetupBuild.PayloadBytes == 0) return null;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                if (stream == null) throw new InvalidDataException("The complete setup is missing its app package.");
            return new Package { Version = "v" + SetupBuild.Version, Url = "", Size = SetupBuild.PayloadBytes,
                Hash = SetupBuild.PayloadSha256, NeedsRuntime = false, Embedded = true };
        }
        internal static async Task WriteArchive(string target, Package package, IProgress<InstallProgress> progress, CancellationToken token)
        {
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (input == null) throw new InvalidDataException("The complete setup is missing its app package.");
                await CopyVerifiedArchive(input, target, package, progress, token).ConfigureAwait(false);
            }
        }
        internal static async Task CopyVerifiedArchive(Stream input, string target, Package package, IProgress<InstallProgress> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (package.Size <= 0) throw new InvalidDataException("The embedded app package is empty.");
            long copied = 0;
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    copied += count;
                    if (copied > package.Size) throw new InvalidDataException("The embedded app package has an unexpected size.");
                    await output.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                    if (progress != null) progress.Report(new InstallProgress("Preparing Steamy", package.Version + " · app and components", 30 + 55d * copied / package.Size, copied, package.Size));
                }
            }
            if (copied != package.Size) throw new InvalidDataException("The embedded app package is incomplete.");
            await Task.Run(() => InstallerCore.VerifyHash(target, package.Hash, false), token).ConfigureAwait(false);
        }
    }
}
