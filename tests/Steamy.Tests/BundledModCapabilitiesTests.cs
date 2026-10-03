using System.Security.Cryptography;
using System.Text.Json;
using Steamy.Services;

namespace Steamy.Tests;

public sealed class BundledModCapabilitiesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Steamy-capability-" + Guid.NewGuid().ToString("N"));
    private string Executable => Path.Combine(_directory, "DepotDownloaderMod.exe");
    public BundledModCapabilitiesTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void UnpatchedOrReplacedToolsNeverReceiveTheNewFlag()
    {
        File.WriteAllText(Executable, "original");
        List<string> arguments = [];
        Assert.False(BundledModCapabilities.AddRateLimit(arguments, Executable, 5));
        Assert.Empty(arguments);
        WriteMarker();
        Assert.True(BundledModCapabilities.AddRateLimit(arguments, Executable, 5));
        Assert.Equal(["-max-download-speed", "5242880"], arguments);
        File.WriteAllText(Executable, "replacement");
        arguments.Clear();
        Assert.False(BundledModCapabilities.AddRateLimit(arguments, Executable, 5));
        Assert.Empty(arguments);
    }

    [Fact]
    public void UnlimitedAddsNoFlagAndMalformedMarkersAreContained()
    {
        File.WriteAllText(Executable, "original");
        WriteMarker();
        List<string> arguments = [];
        Assert.False(BundledModCapabilities.AddRateLimit(arguments, Executable, 0));
        Assert.Empty(arguments);
        File.WriteAllText(Path.Combine(_directory, BundledModCapabilities.MarkerFileName), "{not json");
        Assert.False(BundledModCapabilities.SupportsRateLimit(Executable));
    }

    [Fact]
    public void FrameworkDependentMarkerAlsoChecksItsDll()
    {
        File.WriteAllText(Executable, "original");
        var dll = Path.Combine(_directory, "DepotDownloaderMod.dll");
        File.WriteAllText(dll, "patched");
        WriteMarker(singleFile: false, Hash(dll));
        Assert.True(BundledModCapabilities.SupportsRateLimit(Executable));
        File.WriteAllText(dll, "upstream replacement");
        Assert.False(BundledModCapabilities.SupportsRateLimit(Executable));
    }

    [Fact]
    public void TelemetryRequiresItsOwnCapabilityAndMatchingBinary()
    {
        File.WriteAllText(Executable, "original");
        WriteMarker(); Assert.False(BundledModCapabilities.SupportsProgress(Executable));
        WriteMarker(progressTelemetry: true); Assert.True(BundledModCapabilities.SupportsProgress(Executable));
        File.WriteAllText(Executable, "replacement"); Assert.False(BundledModCapabilities.SupportsProgress(Executable));
    }

    [Fact]
    public void OwnForkFlagsRequireItsIdentityVersionAndVerifiedExecutableHash()
    {
        File.WriteAllText(Executable, "original");
        WriteForkMarker();
        Assert.True(BundledModCapabilities.SupportsOwnFork(Executable));
        Assert.True(BundledModCapabilities.SupportsGracefulStop(Executable));

        File.WriteAllText(Executable, "replacement");
        Assert.False(BundledModCapabilities.SupportsOwnFork(Executable));
        Assert.False(BundledModCapabilities.SupportsGracefulStop(Executable));
    }
    private void WriteMarker(bool singleFile = true, string? dllSha256 = null, bool progressTelemetry = false) =>
        File.WriteAllText(Path.Combine(_directory, BundledModCapabilities.MarkerFileName), JsonSerializer.Serialize(new
        { schemaVersion = 1, maxDownloadSpeed = true, progressTelemetry, singleFile, exeSha256 = Hash(Executable), dllSha256 }));
    private void WriteForkMarker() =>
        File.WriteAllText(Path.Combine(_directory, BundledModCapabilities.MarkerFileName), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, forkName = "Steamy DepotDownloaderMod", forkVersion = "1.0.0",
            sourceSha256 = new string('A', 64), maxDownloadSpeed = true, progressTelemetry = true,
            gracefulStop = true, singleFile = true, exeSha256 = Hash(Executable)
        }));
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    public void Dispose() => Directory.Delete(_directory, true);
}
