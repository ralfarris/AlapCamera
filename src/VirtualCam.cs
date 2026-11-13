using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace AlapCamera
{
    sealed class VirtualCam : IDisposable
    {
        public const string DeviceName = "AlapCamera";
        const string UnityClsid = "{5C2CD55C-92AD-4999-8666-912BD3E70010}";
        const string VideoCat = "{860BB310-5D01-11D0-BD3B-00A0C911CE86}";

        const uint FILE_MAP_ALL_ACCESS = 0xF001F;
        const uint PAGE_READWRITE = 0x04;
        const uint INFINITE = 0xFFFFFFFF;
        const uint WAIT_OBJECT_0 = 0;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern IntPtr CreateMutexA(IntPtr lpMutexAttributes, bool bInitialOwner, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReleaseMutex(IntPtr hMutex);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern IntPtr CreateEventA(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern IntPtr CreateFileMappingA(IntPtr hFile, IntPtr lpAttributes, uint flProtect, uint dwMaxHigh, uint dwMaxLow, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwOffsetHigh, uint dwOffsetLow, UIntPtr dwNumberOfBytesToMap);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct SharedMemHeader
        {
            public int maxSize;
            public int width;
            public int height;
            public int stride;
            public int format;     // 0 = FORMAT_UINT8 (RGBA32)
            public int resizemode; // 1 = linear
            public int mirrormode; // 0 = disabled
            public int timeout;    // ms
        }

        IntPtr _mutex;
        IntPtr _wantEvent;
        IntPtr _sentEvent;
        IntPtr _map;
        IntPtr _view;
        int _maxFrameBytes;
        byte[] _rgbaBuf;
        readonly MfVirtualCam _mf = new MfVirtualCam();

        public bool IsRunning { get; private set; }
        public string BackendName { get; private set; }

        public static bool EnsureRegistered()
        {
            try
            {
                if (IsHklmRegistered()) return true;

                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "AlapCamera", "driver");
                Directory.CreateDirectory(dir);

                string dll64 = Path.Combine(dir, "UnityCaptureFilter64.dll");
                string dll32 = Path.Combine(dir, "UnityCaptureFilter32.dll");
                ExtractResource("AlapCamera.Drivers.UnityCaptureFilter64.dll", dll64);
                ExtractResource("AlapCamera.Drivers.UnityCaptureFilter32.dll", dll32);

                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                RunRegsvr(Path.Combine(winDir, "System32", "regsvr32.exe"), dll64);
                string sysWow = Path.Combine(winDir, "SysWOW64", "regsvr32.exe");
                if (File.Exists(sysWow) && File.Exists(dll32))
                    RunRegsvr(sysWow, dll32);

                string[] subPaths = new[]
                {
                    @"SOFTWARE\Classes\CLSID\" + VideoCat + @"\Instance\" + UnityClsid,
                    @"SOFTWARE\Classes\CLSID\" + UnityClsid,
                    @"SOFTWARE\WOW6432Node\Classes\CLSID\" + VideoCat + @"\Instance\" + UnityClsid,
                    @"SOFTWARE\WOW6432Node\Classes\CLSID\" + UnityClsid
                };
                foreach (string sp in subPaths)
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(sp, true))
                    {
                        if (k != null)
                        {
                            k.SetValue("FriendlyName", DeviceName, RegistryValueKind.String);
                            k.SetValue("", DeviceName, RegistryValueKind.String);
                        }
                    }
                }

                // Clean up any HKCU shadow keys so HKLM is used cleanly everywhere
                foreach (string cu in new[]
                {
                    @"Software\Classes\CLSID\" + VideoCat + @"\Instance\" + UnityClsid,
                    @"Software\Classes\CLSID\" + UnityClsid
                })
                {
                    try { Registry.CurrentUser.DeleteSubKeyTree(cu, false); } catch { }
                }

                return IsHklmRegistered();
            }
            catch
            {
                return false;
            }
        }

        public static bool IsHklmRegistered()
        {
            try
            {
                string inst = @"SOFTWARE\Classes\CLSID\" + VideoCat + @"\Instance\" + UnityClsid;
                string inproc = @"SOFTWARE\Classes\CLSID\" + UnityClsid + @"\InprocServer32";
                using (var k1 = Registry.LocalMachine.OpenSubKey(inst, false))
                using (var k2 = Registry.LocalMachine.OpenSubKey(inproc, false))
                {
                    if (k1 == null || k2 == null) return false;
                    string fn = k1.GetValue("FriendlyName") as string;
                    string dll = k2.GetValue("") as string;
                    return fn == DeviceName && !string.IsNullOrEmpty(dll) && File.Exists(dll);
                }
            }
            catch
            {
                return false;
            }
        }

        static void ExtractResource(string resName, string destPath)
        {
            if (File.Exists(destPath) && new FileInfo(destPath).Length > 100000) return;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName))
            {
                if (s == null) return;
                using (var fs = File.Create(destPath))
                    s.CopyTo(fs);
            }
        }

        static void RunRegsvr(string regsvrExe, string dllPath)
        {
            if (!File.Exists(regsvrExe) || !File.Exists(dllPath)) return;
            var psi = new ProcessStartInfo(regsvrExe, "/s \"" + dllPath + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
                if (p != null) p.WaitForExit(10000);
        }

        public bool Start(int maxWidth = 1920, int maxHeight = 1080)
        {
            Stop();

            if (MfVirtualCam.IsSupported() && _mf.Start(DeviceName))
            {
                BackendName = "MF VirtualCam (Streamlabs/OBS/Zoom)";
                IsRunning = true;
                return true;
            }

            BackendName = "DirectShow";
            EnsureRegistered();
            _maxFrameBytes = maxWidth * maxHeight * 4;
            int totalBytes = Marshal.SizeOf(typeof(SharedMemHeader)) + _maxFrameBytes;

            _mutex = CreateMutexA(IntPtr.Zero, false, "UnityCapture_Mutx0");
            _wantEvent = CreateEventA(IntPtr.Zero, false, false, "UnityCapture_Want0");
            _sentEvent = CreateEventA(IntPtr.Zero, false, false, "UnityCapture_Sent0");
            _map = CreateFileMappingA(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE, 0, (uint)totalBytes, "UnityCapture_Data0");
            if (_map == IntPtr.Zero) return false;

            _view = MapViewOfFile(_map, FILE_MAP_ALL_ACCESS, 0, 0, (UIntPtr)totalBytes);
            if (_view == IntPtr.Zero) { Stop(); return false; }

            var hdr = new SharedMemHeader
            {
                maxSize = _maxFrameBytes,
                width = 0,
                height = 0,
                stride = 0,
                format = 0,
                resizemode = 1,
                mirrormode = 0,
                timeout = 1000
            };
            Marshal.StructureToPtr(hdr, _view, false);
            IsRunning = true;
            return true;
        }

        public unsafe void SendBgr24(byte[] bgr, int width, int height)
        {
            if (_mf.IsRunning)
            {
                _mf.SendBgr24(bgr, width, height);
                return;
            }
            if (!IsRunning || _view == IntPtr.Zero || bgr == null) return;
            int need = width * height * 4;
            if (need > _maxFrameBytes) return;
            if (_rgbaBuf == null || _rgbaBuf.Length < need) _rgbaBuf = new byte[need];

            // Convert top-down BGR24 -> bottom-up RGBA32 (UnityCapture FORMAT_UINT8 convention)
            int srcStride = width * 3;
            int dstStride = width * 4;
            fixed (byte* pSrc = bgr)
            fixed (byte* pDst = _rgbaBuf)
            {
                for (int y = 0; y < height; y++)
                {
                    byte* s = pSrc + y * srcStride;
                    byte* d = pDst + (height - 1 - y) * dstStride;
                    for (int x = 0; x < width; x++)
                    {
                        d[0] = s[2]; // R
                        d[1] = s[1]; // G
                        d[2] = s[0]; // B
                        d[3] = 255;  // A
                        s += 3;
                        d += 4;
                    }
                }
            }

            if (_mutex != IntPtr.Zero && WaitForSingleObject(_mutex, 10) != WAIT_OBJECT_0)
                return;

            try
            {
                var hdr = (SharedMemHeader*)_view;
                if (hdr->maxSize > 0 && need > hdr->maxSize) return;
                hdr->width = width;
                hdr->height = height;
                hdr->stride = width;
                hdr->format = 0;
                hdr->resizemode = 1;
                hdr->mirrormode = 0;
                hdr->timeout = 1000;

                byte* dstPixels = (byte*)_view + sizeof(SharedMemHeader);
                Marshal.Copy(_rgbaBuf, 0, (IntPtr)dstPixels, need);
            }
            finally
            {
                if (_mutex != IntPtr.Zero) ReleaseMutex(_mutex);
            }

            if (_sentEvent != IntPtr.Zero) SetEvent(_sentEvent);
        }

        public void Stop()
        {
            IsRunning = false;
            _mf.Stop();
            if (_view != IntPtr.Zero) { UnmapViewOfFile(_view); _view = IntPtr.Zero; }
            if (_map != IntPtr.Zero) { CloseHandle(_map); _map = IntPtr.Zero; }
            if (_wantEvent != IntPtr.Zero) { CloseHandle(_wantEvent); _wantEvent = IntPtr.Zero; }
            if (_sentEvent != IntPtr.Zero) { CloseHandle(_sentEvent); _sentEvent = IntPtr.Zero; }
            if (_mutex != IntPtr.Zero) { CloseHandle(_mutex); _mutex = IntPtr.Zero; }
        }

        public void Dispose() { Stop(); }
    }
}
