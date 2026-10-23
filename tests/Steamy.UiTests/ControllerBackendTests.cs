using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using Steamy.ControllerProtocol;
using Steamy.Controls;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class ControllerBackendTests
{
    [Fact]
    public async Task RealHeadlessHostRequiresHandshakeAndClosesOnInvalidFrames()
    {
        Assert.True(File.Exists(ControllerBackendService.Executable), "Build and import ControllerHost before running this integration test.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(Path.GetTempPath(), "Steamy-Controller-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var name = "Steamy.Controller.v1." + Guid.NewGuid().ToString("N");
        using var host = Launch(name, secret, root);
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(8000, timeout.Token);
            await ControllerWire.WriteAsync(pipe, new ControllerRequest(1, 1, "hello", secret), timeout.Token);
            var hello = await ControllerWire.ReadAsync<ControllerResponse>(pipe, timeout.Token);
            Assert.True(hello.Ok); Assert.False(hello.State!.Running); Assert.Empty(hello.State.Devices);
            // Driver detection creates only isolated host configuration and does not start/open controllers.
            await ControllerWire.WriteAsync(pipe, new ControllerRequest(1, 2, "drivers"), timeout.Token);
            var drivers = await ControllerWire.ReadAsync<ControllerResponse>(pipe, timeout.Token);
            Assert.True(drivers.Ok, drivers.Error); Assert.False(drivers.State!.Running);
            Assert.Empty(drivers.State.Devices); Assert.True(Directory.Exists(Path.Combine(root, "Profiles")));
            await pipe.WriteAsync(new byte[] { 1, 0, 1, 0 }, timeout.Token); await pipe.FlushAsync(timeout.Token); // 65537, rejected before allocation.
            await host.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, host.ExitCode);
        }
        finally { if (!host.HasExited) host.Kill(); }
        var secondName = "Steamy.Controller.v1." + Guid.NewGuid().ToString("N");
        using var unauthenticated = Launch(secondName, secret, root);
        await using var wrong = new NamedPipeClientStream(".", secondName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await wrong.ConnectAsync(8000, timeout.Token);
            await ControllerWire.WriteAsync(wrong, new ControllerRequest(1, 1, "hello", new string('0', 64)), timeout.Token);
            await Assert.ThrowsAnyAsync<IOException>(() => ControllerWire.ReadAsync<ControllerResponse>(wrong, timeout.Token));
            await unauthenticated.WaitForExitAsync(timeout.Token);
        }
        finally { if (!unauthenticated.HasExited) unauthenticated.Kill(); }
    }
    private static Process Launch(string name, string secret, string root)
    {
        var info = new ProcessStartInfo(ControllerBackendService.Executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(ControllerBackendService.Executable)! };
        info.Environment["STEAMY_CONTROLLER_PIPE"] = name; info.Environment["STEAMY_CONTROLLER_SECRET"] = secret;
        info.Environment["STEAMY_CONTROLLER_PARENT"] = Environment.ProcessId.ToString(); info.Environment["STEAMY_CONTROLLER_TEST_DATA"] = root;
        return Process.Start(info)!;
    }
    [Fact]
    public void HeldButtonsOnReconnectAreIgnoredAndDirectionsRepeatAtAControlledRate()
    {
        var repeat = new GamepadRepeat(); var neutral = new ControllerInput(0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(GamepadAction.None, repeat.Feed(neutral with { Buttons = 0x1000 }, 0));
        Assert.Equal(GamepadAction.None, repeat.Feed(neutral, 1));
        Assert.Equal(GamepadAction.Accept, repeat.Feed(neutral with { Buttons = 0x1000 }, 2));
        Assert.Equal(GamepadAction.None, repeat.Feed(neutral with { Buttons = 0x1000 }, 1000));
        Assert.Equal(GamepadAction.Down, repeat.Feed(neutral with { LY = -.9f }, 1001));
        Assert.Equal(GamepadAction.None, repeat.Feed(neutral with { LY = -.9f }, 1200));
        Assert.Equal(GamepadAction.Down, repeat.Feed(neutral with { LY = -.9f }, 1350));
        Assert.Equal(GamepadAction.None, repeat.Feed(neutral with { LY = -.9f }, 1390));
        repeat.Feed(null, 1400);
        Assert.Equal(GamepadAction.None, repeat.Feed(neutral with { Buttons = 0x2000 }, 1401));
    }
    [Fact]
    public async Task InvalidWireSizesAndMismatchedResponsesAreRejected()
    {
        await using var stream = new MemoryStream(new byte[] { 0xff, 0xff, 0xff, 0x7f });
        await Assert.ThrowsAsync<InvalidDataException>(() => ControllerWire.ReadAsync<ControllerResponse>(stream, default));
        Assert.Throws<InvalidDataException>(() => ControllerBackendService.Validate(new(2, 1, true, ""), 1));
        Assert.Throws<InvalidDataException>(() => ControllerBackendService.Validate(new(1, 2, true, ""), 1));
    }
    [Fact]
    public void DriverInstallerMustPassWindowsTrustAndPublisherChecks()
    {
        Assert.False(ControllerDrivers.VerifySignature(typeof(ControllerBackendTests).Assembly.Location));
        Assert.Throws<ArgumentException>(() => ControllerDrivers.Package("untrusted"));
        if (Environment.GetEnvironmentVariable("STEAMY_DRIVER_FIXTURES") is { Length: > 0 } directory)
        {
            foreach (var driver in new[] { "ViGEmBus", "HidHide" })
            {
                var package = ControllerDrivers.Package(driver); var path = Path.Combine(directory, Path.GetFileName(new Uri(package.Url).LocalPath));
                Assert.True(File.Exists(path)); Assert.Equal(package.Hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
                Assert.True(ControllerDrivers.VerifySignature(path), driver + " must pass the app's own Windows trust/publisher checks.");
            }
        }
    }
}
