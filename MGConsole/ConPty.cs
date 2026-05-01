using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MGConsole
{
    // Minimal Win32 ConPTY (pseudoconsole) wrapper.
    // Spawns a process attached to a pseudo console so it can use the full
    // Win32 Console API (cursor positioning, colors, alternate screen, etc.)
    // and emits its rendering as VT/ANSI escape sequences over a pipe.
    internal sealed class ConPty : IDisposable
    {
        private IntPtr _hPC = IntPtr.Zero;
        private IntPtr _attrList = IntPtr.Zero;
        private PROCESS_INFORMATION _pi;

        public Stream Input { get; private set; } = Stream.Null;   // we write -> pty stdin
        public Stream Output { get; private set; } = Stream.Null;  // we read  <- pty stdout

        public bool HasExited
        {
            get
            {
                if (_pi.hProcess == IntPtr.Zero) return true;
                return WaitForSingleObject(_pi.hProcess, 0) == 0; // WAIT_OBJECT_0
            }
        }

        public void Start(string command, short cols, short rows)
        {
            if (!CreatePipe(out IntPtr inputReadSide, out IntPtr inputWriteSide, IntPtr.Zero, 0))
                throw new InvalidOperationException("CreatePipe (stdin) failed: " + Marshal.GetLastWin32Error());
            if (!CreatePipe(out IntPtr outputReadSide, out IntPtr outputWriteSide, IntPtr.Zero, 0))
                throw new InvalidOperationException("CreatePipe (stdout) failed: " + Marshal.GetLastWin32Error());

            COORD size = new COORD { X = cols, Y = rows };
            int hr = CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out _hPC);
            if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole failed 0x{hr:X}");

            // The pty owns these ends now.
            CloseHandle(inputReadSide);
            CloseHandle(outputWriteSide);

            // Build a thread attribute list with the pseudoconsole handle.
            IntPtr lpSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
            _attrList = Marshal.AllocHGlobal(lpSize);
            if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref lpSize))
                throw new InvalidOperationException("InitializeProcThreadAttributeList failed: " + Marshal.GetLastWin32Error());
            if (!UpdateProcThreadAttribute(_attrList, 0,
                    (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hPC,
                    (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("UpdateProcThreadAttribute failed: " + Marshal.GetLastWin32Error());

            STARTUPINFOEX si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = _attrList;

            if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, null, ref si, out _pi))
                throw new InvalidOperationException("CreateProcess failed: " + Marshal.GetLastWin32Error());

            Input = new FileStream(new SafeFileHandle(inputWriteSide, true), FileAccess.Write);
            Output = new FileStream(new SafeFileHandle(outputReadSide, true), FileAccess.Read);
        }

        public void WaitForExit()
        {
            if (_pi.hProcess != IntPtr.Zero)
                WaitForSingleObject(_pi.hProcess, 0xFFFFFFFF); // INFINITE
        }

        public void Resize(short cols, short rows)
        {
            if (_hPC != IntPtr.Zero)
                ResizePseudoConsole(_hPC, new COORD { X = cols, Y = rows });
        }

        public void Dispose()
        {
            // ConPTY teardown order matters. Per the Microsoft sample / docs:
            //   1) Close the input WRITE pipe (our side) so ClosePseudoConsole
            //      doesn't synchronously wait forever for the host to finish
            //      reading from a still-open input handle.
            //   2) Close the pseudoconsole — this signals conhost to shut down
            //      and (if still alive) terminates the client process.
            //   3) Close the output READ pipe.
            //   4) Free the attribute list and process/thread handles.

            try { Input?.Dispose(); } catch { }
            Input = Stream.Null;

            if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }

            try { Output?.Dispose(); } catch { }
            Output = Stream.Null;

            if (_attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(_attrList);
                Marshal.FreeHGlobal(_attrList);
                _attrList = IntPtr.Zero;
            }
            if (_pi.hThread  != IntPtr.Zero) { CloseHandle(_pi.hThread);  _pi.hThread  = IntPtr.Zero; }
            if (_pi.hProcess != IntPtr.Zero) { CloseHandle(_pi.hProcess); _pi.hProcess = IntPtr.Zero; }
        }

        // ------------- P/Invoke -------------

        const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            [In] ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        private static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);
    }
}
