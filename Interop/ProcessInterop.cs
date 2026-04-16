using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RightSpeak.Interop;

internal static class ProcessInterop
{
    private const uint Th32CsSnapProcess = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    public static string? GetParentProcessName()
    {
        var currentProcessId = (uint)Environment.ProcessId;
        var parentProcessId = GetParentProcessId(currentProcessId);
        if (parentProcessId is null)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)parentProcessId.Value);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static uint? GetParentProcessId(uint processId)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == 0 || snapshot == -1)
        {
            return null;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                dwSize = (uint)Marshal.SizeOf<ProcessEntry32>()
            };

            if (!Process32First(snapshot, ref entry))
            {
                return null;
            }

            do
            {
                if (entry.th32ProcessID == processId)
                {
                    return entry.th32ParentProcessID;
                }
            }
            while (Process32Next(snapshot, ref entry));

            return null;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
