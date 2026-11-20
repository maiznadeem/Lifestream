using System.Runtime.InteropServices;
using System.Text;

namespace Lifestream.Isolated;

/// <summary>The Win32 calls of the isolation tool.</summary>
internal static unsafe class Native
{
    public const uint GenericAll = 0x10000000;
    public const uint DesktopReadObjects = 0x0001;
    public const uint DesktopEnumerate = 0x0040;
    public const int UoiName = 2;

    public const uint CreateSuspended = 0x00000004;
    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const uint CreateNoWindow = 0x08000000;
    public const int StartfUseStdHandles = 0x00000100;
    public const int StartfForceOffFeedback = 0x00000080;

    public const uint JobObjectAssignProcess = 0x0001;
    public const uint JobObjectQuery = 0x0004;
    public const uint JobObjectTerminate = 0x0008;
    public const uint JobObjectAllAccess = 0x1F003F;
    public const int JobObjectBasicProcessIdList = 3;
    public const int JobObjectBasicUiRestrictions = 4;
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint JobLimitAffinity = 0x00000010;
    public const uint JobLimitPriorityClass = 0x00000020;
    public const uint JobLimitKillOnJobClose = 0x00002000;
    public const uint BelowNormalPriorityClass = 0x00004000;

    public const uint UiLimitHandles = 0x0001;
    public const uint UiLimitReadClipboard = 0x0002;
    public const uint UiLimitWriteClipboard = 0x0004;
    public const uint UiLimitSystemParameters = 0x0008;
    public const uint UiLimitDisplaySettings = 0x0010;
    public const uint UiLimitDesktop = 0x0040;
    public const uint UiLimitExitWindows = 0x0080;

    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint ProcessTerminate = 0x0001;
    public const uint Synchronize = 0x00100000;
    public const uint WaitTimeout = 0x102;
    public const uint Infinite = 0xFFFFFFFF;
    public const uint HandleFlagInherit = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Count;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ExtendedLimit
    {
        public BasicLimit Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int CaretLeft, CaretTop, CaretRight, CaretBottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public int X, Y;
    }

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    public delegate void WinEventProc(IntPtr hook, uint @event, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, uint flags, uint access, IntPtr sa);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenDesktopW(string name, uint flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll")]
    public static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDesktop(uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetUserObjectInformationW(IntPtr handle, int index, byte[] buffer, int length, out int needed);

    [DllImport("user32.dll")]
    public static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindowsProc proc, IntPtr param);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    public static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern bool PeekMessageW(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessageW(uint thread, uint msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint ms, uint mask);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateJobObjectW(IntPtr sa, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenJobObjectW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(IntPtr job, int infoClass, void* info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryInformationJobObject(IntPtr job, int infoClass, void* info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    public static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes sa, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr sa, bool manual, bool initial, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenEventW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll")]
    public static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWaitableTimerExW(IntPtr sa, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr arg, bool resume);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    public static string ObjectName(IntPtr handle)
    {
        var buffer = new byte[512];
        return GetUserObjectInformationW(handle, UoiName, buffer, buffer.Length, out var needed)
            ? Encoding.Unicode.GetString(buffer, 0, Math.Max(0, needed - 2))
            : string.Empty;
    }

    /// <summary>The name of the desktop the calling thread is on.</summary>
    public static string ThreadDesktopName() => ObjectName(GetThreadDesktop(GetCurrentThreadId()));

    /// <summary>The name of the desktop that receives input now, or null when this process may not open it (locked, UAC prompt).</summary>
    public static string? InputDesktopName()
    {
        var input = OpenInputDesktop(0, false, DesktopReadObjects);
        if (input == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ObjectName(input);
        }
        finally
        {
            CloseDesktop(input);
        }
    }

    public static string ProcessName(uint pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            return $"pid{pid}";
        }

        try
        {
            var name = new StringBuilder(1024);
            var size = name.Capacity;
            return QueryFullProcessImageNameW(process, 0, name, ref size) ? Path.GetFileName(name.ToString()) : $"pid{pid}";
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public static string ClassName(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassNameW(hwnd, name, name.Capacity) > 0 ? name.ToString() : "?";
    }
}
