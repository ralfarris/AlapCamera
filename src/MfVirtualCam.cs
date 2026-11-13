using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace AlapCamera
{
    internal sealed class MfVirtualCam : IDisposable
    {
        public const string ClsidStr = "{A1A9C0DE-0001-4000-8000-414C41504341}";
        const string ResourceName = "AlapCamera.Drivers.AlapMfCam64.dll";
        const int MaxSharedBytes = 64 + 1920 * 1080 * 3;
        const uint MF_VERSION = 0x00020070;

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct SharedFrameHeader
        {
            public int seq;
            public int width;
            public int height;
            public int stride;
            public int format; // 0 = BGR24
            public int active;
            public long timestamp100ns;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string lpLibFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateFileMappingW(
            IntPtr hFile, IntPtr lpAttributes, uint flProtect,
            uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr MapViewOfFile(
            IntPtr hFileMappingObject, uint dwDesiredAccess,
            uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("mfplat.dll")]
        static extern int MFStartup(uint version, uint dwFlags);

        [DllImport("mfplat.dll")]
        static extern int MFShutdown();

        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        delegate int FnMFCreateVirtualCamera(
            int type, int lifetime, int access,
            string friendlyName, string sourceId,
            IntPtr categories, uint categoryCount,
            out IntPtr virtualCamera);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate int FnVcamStart(IntPtr self, IntPtr pCallback);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate int FnVcamShutdown(IntPtr self);

        IntPtr _hMfSensor;
        IntPtr _vcam;
        IntPtr _hFile = new IntPtr(-1);
        IntPtr _hMap;
        IntPtr _view;
        bool _mfStarted;
        int _seq;

        public bool IsRunning { get; private set; }
        public string LastError { get; private set; }

        public static bool IsSupported()
        {
            string p = Path.Combine(Environment.SystemDirectory, "mfsensorgroup.dll");
            return File.Exists(p);
        }

        public static bool EnsureRegistered()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "AlapCamera", "driver");
                Directory.CreateDirectory(dir);
                string dllPath = Path.Combine(dir, "AlapMfCam64.dll");

                byte[] resBytes;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                {
                    if (s == null) return false;
                    resBytes = new byte[s.Length];
                    int r = 0;
                    while (r < resBytes.Length)
                    {
                        int n = s.Read(resBytes, r, resBytes.Length - r);
                        if (n <= 0) break;
                        r += n;
                    }
                }

                bool needCopy = !File.Exists(dllPath) || new FileInfo(dllPath).Length != resBytes.Length;
                if (needCopy)
                {
                    try { File.WriteAllBytes(dllPath, resBytes); } catch { }
                }

                bool regOk = false;
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + ClsidStr + @"\InprocServer32", false))
                {
                    if (k != null)
                    {
                        string val = k.GetValue("") as string;
                        regOk = string.Equals(val, dllPath, StringComparison.OrdinalIgnoreCase) && File.Exists(dllPath);
                    }
                }

                if (!regOk && File.Exists(dllPath))
                {
                    string regsvr = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "regsvr32.exe");
                    var psi = new ProcessStartInfo(regsvr, "/s \"" + dllPath + "\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                        if (p != null) p.WaitForExit(10000);
                }

                // Grant Everyone read/write on frame_shm.bin directory if possible
                try
                {
                    var psi = new ProcessStartInfo("icacls.exe", "\"" + dir + "\" /grant *S-1-1-0:(OI)(CI)M /Q")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                        if (p != null) p.WaitForExit(5000);
                }
                catch { }

                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + ClsidStr + @"\InprocServer32", false))
                    return k != null;
            }
            catch
            {
                return false;
            }
        }

        public unsafe bool Start(string friendlyName)
        {
            Stop();
            LastError = null;
            try
            {
                if (!IsSupported()) { LastError = "mfsensorgroup.dll missing"; return false; }
                if (!EnsureRegistered()) { LastError = "AlapMfCam64.dll not registered"; return false; }

                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "AlapCamera", "driver");
                Directory.CreateDirectory(dir);
                string shmPath = Path.Combine(dir, "frame_shm.bin");

                // GENERIC_READ | GENERIC_WRITE = 0xC0000000, SHARE_R|W|D = 7, OPEN_ALWAYS = 4
                _hFile = CreateFileW(shmPath, 0xC0000000u, 7u, IntPtr.Zero, 4u, 0x80u, IntPtr.Zero);
                if (_hFile == new IntPtr(-1))
                {
                    LastError = "CreateFileW shm failed: " + Marshal.GetLastWin32Error();
                    return false;
                }

                _hMap = CreateFileMappingW(_hFile, IntPtr.Zero, 0x04u /*PAGE_READWRITE*/, 0, (uint)MaxSharedBytes, null);
                if (_hMap == IntPtr.Zero)
                {
                    LastError = "CreateFileMappingW failed: " + Marshal.GetLastWin32Error();
                    Stop();
                    return false;
                }

                _view = MapViewOfFile(_hMap, 0x000F001Fu /*FILE_MAP_ALL_ACCESS*/, 0, 0, (UIntPtr)MaxSharedBytes);
                if (_view == IntPtr.Zero)
                {
                    LastError = "MapViewOfFile failed";
                    Stop();
                    return false;
                }

                var hdr = (SharedFrameHeader*)_view;
                hdr->seq = 0;
                hdr->width = 0;
                hdr->height = 0;
                hdr->stride = 0;
                hdr->format = 0;
                hdr->active = 0;

                if (MFStartup(MF_VERSION, 1 /*MFSTARTUP_NOSOCKET*/) >= 0)
                    _mfStarted = true;

                _hMfSensor = LoadLibraryW(Path.Combine(Environment.SystemDirectory, "mfsensorgroup.dll"));
                if (_hMfSensor == IntPtr.Zero) { LastError = "LoadLibrary mfsensorgroup failed"; Stop(); return false; }

                IntPtr pCreate = GetProcAddress(_hMfSensor, "MFCreateVirtualCamera");
                if (pCreate == IntPtr.Zero) { LastError = "MFCreateVirtualCamera missing"; Stop(); return false; }

                var fnCreate = (FnMFCreateVirtualCamera)Marshal.GetDelegateForFunctionPointer(pCreate, typeof(FnMFCreateVirtualCamera));
                int hr = fnCreate(
                    0, // MFVirtualCameraType_SoftwareCameraSource
                    0, // MFVirtualCameraLifetime_Session
                    0, // MFVirtualCameraAccess_CurrentUser
                    friendlyName,
                    ClsidStr,
                    IntPtr.Zero,
                    0,
                    out _vcam);
                if (hr < 0 || _vcam == IntPtr.Zero)
                {
                    LastError = "MFCreateVirtualCamera hr=0x" + hr.ToString("X8");
                    Stop();
                    return false;
                }

                // IMFVirtualCamera::Start is vtable slot 36
                IntPtr vtbl = Marshal.ReadIntPtr(_vcam);
                IntPtr pStart = Marshal.ReadIntPtr(vtbl, 36 * IntPtr.Size);
                var fnStart = (FnVcamStart)Marshal.GetDelegateForFunctionPointer(pStart, typeof(FnVcamStart));
                hr = fnStart(_vcam, IntPtr.Zero);
                if (hr < 0)
                {
                    LastError = "IMFVirtualCamera::Start hr=0x" + hr.ToString("X8");
                    Stop();
                    return false;
                }

                IsRunning = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Stop();
                return false;
            }
        }

        public unsafe void SendBgr24(byte[] bgr, int width, int height)
        {
            if (!IsRunning || _view == IntPtr.Zero || bgr == null) return;
            if (width <= 0 || height <= 0 || width > 1920 || height > 1080) return;

            int bytes = width * height * 3;
            if (64 + bytes > MaxSharedBytes || bgr.Length < bytes) return;

            var hdr = (SharedFrameHeader*)_view;
            int s = (_seq + 1) | 1; // odd = writing
            hdr->seq = s;
            Thread.MemoryBarrier();

            hdr->width = width;
            hdr->height = height;
            hdr->stride = width * 3;
            hdr->format = 0;
            hdr->active = 1;
            Marshal.Copy(bgr, 0, IntPtr.Add(_view, 64), bytes);

            Thread.MemoryBarrier();
            _seq = s + 1; // even = ready
            hdr->seq = _seq;
        }

        public unsafe void Stop()
        {
            IsRunning = false;
            if (_view != IntPtr.Zero)
            {
                try { ((SharedFrameHeader*)_view)->active = 0; } catch { }
                UnmapViewOfFile(_view);
                _view = IntPtr.Zero;
            }
            if (_hMap != IntPtr.Zero) { CloseHandle(_hMap); _hMap = IntPtr.Zero; }
            if (_hFile != new IntPtr(-1)) { CloseHandle(_hFile); _hFile = new IntPtr(-1); }

            if (_vcam != IntPtr.Zero)
            {
                try
                {
                    IntPtr vtbl = Marshal.ReadIntPtr(_vcam);
                    IntPtr pShutdown = Marshal.ReadIntPtr(vtbl, 43 * IntPtr.Size);
                    var fnShutdown = (FnVcamShutdown)Marshal.GetDelegateForFunctionPointer(pShutdown, typeof(FnVcamShutdown));
                    fnShutdown(_vcam);
                }
                catch { }
                try { Marshal.Release(_vcam); } catch { }
                _vcam = IntPtr.Zero;
            }

            if (_mfStarted)
            {
                try { MFShutdown(); } catch { }
                _mfStarted = false;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
