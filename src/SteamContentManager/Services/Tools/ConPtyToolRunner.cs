using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SteamContentManager.Services;

/// <summary>
/// Runs a console tool inside a Windows pseudo console (ConPTY).
/// <para>
/// Interactive Rust tools built on <c>dialoguer</c> read their prompt through <c>console</c>,
/// which returns <c>NotConnected / "not a terminal"</c> when stdin is a plain pipe and the process
/// panics on <c>unwrap()</c>. Handing it a redirected stream is therefore impossible: the tool
/// needs a real terminal. ConPTY provides exactly that headlessly, so the answers can be typed in
/// and the output read back.
/// </para>
/// </summary>
internal static class ConPtyToolRunner
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;

    /// <summary>
    /// Forces the child to use the handles given in STARTUPINFO instead of the parent's.
    /// <para>
    /// CreateProcess duplicates the parent's standard handles into the child when the parent's
    /// output is redirected and this flag is missing — even with a pseudoconsole attached. The
    /// child then ignores the pseudoconsole and writes straight into the parent's pipes, which is
    /// why the tool behaved as if it had no terminal (microsoft/terminal#15814).
    /// </para>
    /// </summary>
    private const int StartfUseStdHandles = 0x00000100;

    /// <summary>Attribute slot that attaches a pseudoconsole handle to a new process.</summary>
    private static readonly IntPtr ProcThreadAttributePseudoConsole = new(0x00020016);

    private static readonly TimeSpan ReaderGrace = TimeSpan.FromSeconds(5);
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitPollMilliseconds = 200;

    public static async Task<LocalToolRunResult> RunAsync(
        string executablePath,
        string arguments,
        string workingDirectory,
        string standardInput,
        CancellationToken cancellationToken)
    {
        var commandLine = new StringBuilder(Quote(executablePath));
        if (!string.IsNullOrWhiteSpace(arguments))
            commandLine.Append(' ').Append(arguments);

        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr inputRead = IntPtr.Zero;
        IntPtr inputWrite = IntPtr.Zero;
        IntPtr outputRead = IntPtr.Zero;
        IntPtr outputWrite = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;

        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "console input pipe");

            if (!CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "console output pipe");

            var size = new Coord { X = 120, Y = 30 };
            var hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out pseudoConsole);
            if (hr != 0)
                throw new Win32Exception(hr, "CreatePseudoConsole");

            // The pseudoconsole keeps its own reference to both ends.
            CloseHandle(inputRead);
            inputRead = IntPtr.Zero;
            CloseHandle(outputWrite);
            outputWrite = IntPtr.Zero;

            var attributeSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
            if (attributeSize == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "attribute list size");

            attributeList = Marshal.AllocHGlobal(attributeSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");

            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributePseudoConsole,
                    pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles
                },
                AttributeList = attributeList
            };

            if (!CreateProcess(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startupInfo,
                    out var processInformation))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess");

            processHandle = processInformation.ProcessHandle;
            threadHandle = processInformation.ThreadHandle;

            // Reading has to start before typing: the tool prints its prompt and then blocks.
            var outputTask = ReadConsoleAsync(outputRead);
            outputRead = IntPtr.Zero;

            WriteConsole(inputWrite, standardInput);

            // The console input pipe stays open until the tool has exited. Closing it early makes
            // the pseudoconsole treat it as end-of-input and interrupt the child with Ctrl+C
            // (exit code 0xC000013A) before it ever printed its result.
            await WaitForExitAsync(processHandle, cancellationToken).ConfigureAwait(false);

            GetExitCodeProcess(processHandle, out var exitCode);

            // Closing the pseudoconsole ends the output stream, so the reader can finish.
            ClosePseudoConsole(pseudoConsole);
            pseudoConsole = IntPtr.Zero;

            var output = await CompleteOutputAsync(outputTask).ConfigureAwait(false);
            var plain = ToPlainText(output);

            var succeeded = exitCode == 0;
            return new LocalToolRunResult(
                succeeded,
                unchecked((int)exitCode),
                false,
                succeeded
                    ? $"{Path.GetFileName(executablePath)} finished successfully."
                    : $"{Path.GetFileName(executablePath)} exited with code {exitCode}.",
                plain);
        }
        catch (OperationCanceledException)
        {
            return new LocalToolRunResult(false, null, true, "The run was cancelled.", string.Empty);
        }
        catch (Exception exception)
        {
            return new LocalToolRunResult(false, null, false, $"The tool could not be started: {exception.Message}", string.Empty);
        }
        finally
        {
            if (pseudoConsole != IntPtr.Zero) ClosePseudoConsole(pseudoConsole);
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (inputRead != IntPtr.Zero) CloseHandle(inputRead);
            if (inputWrite != IntPtr.Zero) CloseHandle(inputWrite);
            if (threadHandle != IntPtr.Zero) CloseHandle(threadHandle);
            if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
        }
    }

    private static async Task WaitForExitAsync(IntPtr processHandle, CancellationToken cancellationToken)
    {
        while (true)
        {
            var waited = WaitForSingleObject(processHandle, WaitPollMilliseconds);
            if (waited == WaitObject0)
                return;

            if (waited == WaitTimeout && !cancellationToken.IsCancellationRequested)
                continue;

            if (cancellationToken.IsCancellationRequested)
            {
                TryTerminate(processHandle);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return;
        }
    }

    private static void TryTerminate(IntPtr processHandle)
    {
        try
        {
            TerminateProcess(processHandle, 1);
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    private static async Task<string> CompleteOutputAsync(Task<string> outputTask)
    {
        // The reader finishes as soon as the pseudoconsole closes; a short grace period keeps a
        // stuck pipe from hanging the page forever.
        var completed = await Task.WhenAny(outputTask, Task.Delay(ReaderGrace)).ConfigureAwait(false);
        return completed == outputTask ? await outputTask.ConfigureAwait(false) : string.Empty;
    }

    private static Task<string> ReadConsoleAsync(IntPtr handle) => Task.Run(() =>
    {
        var builder = new StringBuilder();
        var buffer = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();

        using var stream = new FileStream(new SafeFileHandle(handle, ownsHandle: true), FileAccess.Read);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var chars = new char[decoder.GetCharCount(buffer, 0, read)];
            var produced = decoder.GetChars(buffer, 0, read, chars, 0);
            builder.Append(chars, 0, produced);
        }

        return builder.ToString();
    });

    private static void WriteConsole(IntPtr handle, string text)
    {
        try
        {
            // A terminal expects a carriage return for Enter, not a line feed.
            var bytes = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r"));
            using var stream = new FileStream(new SafeFileHandle(handle, ownsHandle: false), FileAccess.Write);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch (Exception)
        {
            // A tool that exits before reading closes the pipe first; not worth reporting.
        }
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    /// <summary>
    /// Strips the terminal control sequences a pseudoconsole emits. Without this the parser would
    /// see colour codes and cursor moves between every label and value.
    /// </summary>
    private static string ToPlainText(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var withoutEscapes = ControlSequence.Replace(raw, string.Empty);
        var cleaned = new StringBuilder();

        foreach (var line in withoutEscapes.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0 && cleaned.Length == 0) continue;
            cleaned.AppendLine(trimmed);
        }

        return cleaned.ToString().Trim();
    }

    private static readonly System.Text.RegularExpressions.Regex ControlSequence = new(
        @"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[@-Z\\-_]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr pipeAttributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
