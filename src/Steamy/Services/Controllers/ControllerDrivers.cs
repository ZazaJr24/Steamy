using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Steamy.Services;

public interface IControllerDrivers { Task InstallAsync(string driver, CancellationToken token = default); }
public sealed class ControllerDrivers : IControllerDrivers
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    internal static (string Url, string Hash) Package(string driver) => driver switch
    {
        "ViGEmBus" => ("https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe", "89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A"),
        "HidHide" => ("https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe", "F4BBBCB82E6258641B887C74BC81C4C5F66E4AA811808DFC304347687B7605F6"),
        _ => throw new ArgumentException("Choose ViGEmBus or HidHide.")
    };
    // Called only by the explicitly labelled Install buttons. No startup/driver detection calls here.
    public async Task InstallAsync(string driver, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidOperationException("This controller package requires Windows 10 2004 or later on x64.");
        var package = Package(driver);
        var directory = Path.Combine(CacheTempService.Root, "controller-installers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, driver + ".exe");
        try
        {
            using var response = await Http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 80 * 1024 * 1024) throw new IOException("Driver download exceeded the size limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, true))
            {
                var buffer = new byte[32768]; var total = 0;
                for (var count = await input.ReadAsync(buffer, token); count > 0; count = await input.ReadAsync(buffer, token))
                { total += count; if (total > 80 * 1024 * 1024) throw new IOException("Driver download exceeded the size limit."); await output.WriteAsync(buffer.AsMemory(0, count), token); }
            }
            // Prevent another process from replacing the verified bytes before the installer starts.
            await using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(locked, token));
            if (hash != package.Hash) throw new IOException("The driver checksum didn't match the official package. Installation was stopped.");
            var verified = await Task.Run(() => VerifySignature(path), token);
            if (!verified) throw new IOException("Windows couldn't verify the Nefarius installer signature. Check your connection and use Driver information.");
            token.ThrowIfCancellationRequested();
            using var installer = await Task.Run(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = directory }), token);
            if (installer is null) throw new IOException("The driver installer didn't start.");
            await installer.WaitForExitAsync(token);
            if (installer.ExitCode is not (0 or 1641 or 3010)) throw new IOException("Driver setup was cancelled or didn't complete. You can try again from this page.");
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223) { throw new InvalidOperationException("Driver installation was cancelled."); }
        finally { try { File.Delete(path); Directory.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    internal static bool VerifySignature(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>()); Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UIChoice = 2, Revocation = 1, UnionChoice = 1, File = pointer, StateAction = 1, Flags = 0x80 };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            if (WinVerifyTrust(new(-1), ref action, ref data) != 0) return false;
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.GetNameInfo(X509NameType.SimpleName, false).Equals("Nefarius Software Solutions e.U.", StringComparison.Ordinal);
        }
        catch (CryptographicException) { return false; }
        finally { data.StateAction = 2; _ = WinVerifyTrust(new(-1), ref action, ref data); Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile
    { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustData
    { public uint Size; public IntPtr Policy, Sip; public uint UIChoice, Revocation, UnionChoice; public IntPtr File; public uint StateAction; public IntPtr State; public IntPtr Url; public uint Flags, UIContext; }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern uint WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
