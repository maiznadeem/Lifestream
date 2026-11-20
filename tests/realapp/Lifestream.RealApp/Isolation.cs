using System.Runtime.InteropServices;
using System.Text;

namespace Lifestream.RealApp;

/// <summary>
/// The driver runs only on an isolated desktop (tests/isolated/README.md, tools/realapp-isolated.ps1): a <c>LifestreamIso-*</c> desktop named by
/// <c>LIFESTREAM_ISOLATED_DESKTOP</c> that is not the desktop receiving input. UI Automation reaches only the desktop it runs on, and the app it
/// launches inherits that desktop. GUI processes are started with STARTF_FORCEOFFFEEDBACK: without it the "app starting" cursor shows on the
/// person's screen for about 1.5 s although the process is on another desktop (measured 2026-11-21).
/// </summary>
internal static partial class Isolation
{
    public static string? WhyNot()
    {
        var expected = Environment.GetEnvironmentVariable("LIFESTREAM_ISOLATED_DESKTOP");
        var mine = Name(GetThreadDesktop(GetCurrentThreadId()));
        if (string.IsNullOrEmpty(expected) || !expected.StartsWith("LifestreamIso-", StringComparison.Ordinal))
        {
            return $"LIFESTREAM_ISOLATED_DESKTOP does not name an isolated LifestreamIso-* desktop (this process is on '{mine}')";
        }

        if (!string.Equals(mine, expected, StringComparison.OrdinalIgnoreCase))
        {
            return $"this process is on the desktop '{mine}', not on '{expected}'";
        }

        var input = OpenInputDesktop(0, false, 0x0001);
        if (input != IntPtr.Zero)
        {
            try
            {
                if (string.Equals(Name(input), mine, StringComparison.OrdinalIgnoreCase))
                {
                    return $"the desktop '{mine}' receives input";
                }
            }
            finally
            {
                CloseDesktop(input);
            }
        }

        return null;
    }

    /// <summary>Starts a GUI process on this process's desktop without the busy cursor, with the given environment; returns its id.</summary>
    public static int StartWithoutFeedback(string exe, string workingDirectory, IDictionary<string, string> environment)
    {
        var block = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            block.Append(key).Append('=').Append(value).Append('\0');
        }

        block.Append('\0');
        var env = Marshal.StringToHGlobalUni(block.ToString());
        try
        {
            var startup = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>(), Flags = 0x00000080 /* STARTF_FORCEOFFFEEDBACK */ };
            var commandLine = Marshal.StringToHGlobalUni($"\"{exe}\""); // writable, as CreateProcessW wants it
            ProcessInformation info;
            try
            {
                if (!CreateProcessW(IntPtr.Zero, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x00000400 /* CREATE_UNICODE_ENVIRONMENT */, env, workingDirectory, ref startup, out info))
                {
                    throw new InvalidOperationException($"CreateProcess failed ({Marshal.GetLastPInvokeError()})");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(commandLine);
            }

            CloseHandle(info.Thread);
            CloseHandle(info.Process);
            return info.ProcessId;
        }
        finally
        {
            Marshal.FreeHGlobal(env);
        }
    }

    private static string Name(IntPtr handle)
    {
        var buffer = new byte[512];
        return GetUserObjectInformationW(handle, 2, buffer, buffer.Length, out var needed) ? Encoding.Unicode.GetString(buffer, 0, Math.Max(0, needed - 2)) : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Count;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(IntPtr application, IntPtr commandLine, IntPtr pa, IntPtr ta, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr env, string? dir, ref StartupInfo startup, out ProcessInformation info);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("user32.dll")]
    private static partial IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(IntPtr desktop);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetThreadDesktop(uint thread);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformationW(IntPtr handle, int index, [Out] byte[] buffer, int length, out int needed);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
