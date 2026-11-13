using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace AlapCamera
{
    sealed class CameraDevice
    {
        public int Index;
        public string Name;
        public string DevicePath;
        public override string ToString() { return Name; }
    }

    sealed class CameraCapture : IDisposable
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsBusy { get; private set; }
        public string CameraName { get; private set; }

        public delegate void FrameHandler(byte[] bgr24, int width, int height);
        public event FrameHandler OnFrame;

        readonly int _index;
        volatile bool _running;
        Thread _thread;
        object _graphObj;
        IMediaControl _mediaCtrl;
        IBaseFilter _srcFilter;
        SampleGrabberCB _cb;
        int _tick;

        static readonly string[] VirtualSinks = {
            "alapcamera", "streamlabs", "lsvcam", "bytecast",
            "unity video capture", "obs virtual", "xsplit", "manycam", "snap camera"
        };
        static readonly string[] SoftwareHints = { "virtual", "camo", "droidcam", "iriun" };

        public static List<CameraDevice> ListDevices()
        {
            var list = new List<CameraDevice>();
            ICreateDevEnum devEnum = null;
            IEnumMoniker enumMon = null;
            try
            {
                devEnum = (ICreateDevEnum)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_SystemDeviceEnum));
                Guid cat = CLSID_VideoInputDeviceCategory;
                if (devEnum.CreateClassEnumerator(ref cat, out enumMon, 0) != 0 || enumMon == null)
                    return list;

                IMoniker[] mons = new IMoniker[1];
                int idx = 0;
                while (enumMon.Next(1, mons, IntPtr.Zero) == 0)
                {
                    string name = "Camera " + idx;
                    string path = "";
                    try
                    {
                        Guid bagId = typeof(IPropertyBag).GUID;
                        object bagObj;
                        mons[0].BindToStorage(null, null, ref bagId, out bagObj);
                        var bag = (IPropertyBag)bagObj;
                        object v;
                        if (bag.Read("FriendlyName", out v, null) == 0 && v != null) name = v.ToString();
                        if (bag.Read("DevicePath", out v, null) == 0 && v != null) path = v.ToString();
                        Marshal.ReleaseComObject(bag);
                    }
                    catch { }
                    Marshal.ReleaseComObject(mons[0]);

                    string low = name.ToLowerInvariant();
                    bool isSink = false;
                    foreach (var s in VirtualSinks) if (low.Contains(s)) { isSink = true; break; }
                    if (!isSink) list.Add(new CameraDevice { Index = idx, Name = name, DevicePath = path });
                    idx++;
                }
            }
            catch { }
            finally
            {
                if (enumMon != null) Marshal.ReleaseComObject(enumMon);
                if (devEnum != null) Marshal.ReleaseComObject(devEnum);
            }
            return list;
        }

        public static int DefaultCameraIndex(List<CameraDevice> cams)
        {
            for (int i = 0; i < cams.Count; i++)
            {
                string low = cams[i].Name.ToLowerInvariant();
                bool sw = false;
                foreach (var h in SoftwareHints) if (low.Contains(h)) { sw = true; break; }
                if (!sw) return i;
            }
            return cams.Count > 0 ? 0 : -1;
        }

        public CameraCapture(int index, string name)
        {
            _index = index;
            CameraName = name ?? ("Camera " + index);
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(RunLoop) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        void RunLoop()
        {
            while (_running)
            {
                if (!OpenAndRun())
                {
                    IsBusy = true;
                    CleanupGraph();
                    Thread.Sleep(1200);
                    continue;
                }
                IsBusy = false;
                while (_running)
                {
                    Thread.Sleep(250);
                    if (_cb != null && (Environment.TickCount - _cb.LastTick) > 3000)
                        break;
                }
                CleanupGraph();
            }
        }

        bool OpenAndRun()
        {
            try
            {
                _srcFilter = BindDeviceByIndex(_index);
                if (_srcFilter == null) return false;

                var graph = (IGraphBuilder)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_FilterGraph));
                _graphObj = graph;
                var capBuilder = (ICaptureGraphBuilder2)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_CaptureGraphBuilder2));
                capBuilder.SetFiltergraph(graph);

                var grabberFilter = (IBaseFilter)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_SampleGrabber));
                var grabber = (ISampleGrabber)grabberFilter;
                var nullRenderer = (IBaseFilter)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_NullRenderer));

                var mt = new AMMediaType
                {
                    majorType = MEDIATYPE_Video,
                    subType = MEDIASUBTYPE_RGB24,
                    formatType = FORMAT_VideoInfo
                };
                grabber.SetMediaType(mt);

                graph.AddFilter(_srcFilter, "Video Source");
                graph.AddFilter(grabberFilter, "Sample Grabber");
                graph.AddFilter(nullRenderer, "Null Renderer");

                Guid pinCat = PIN_CATEGORY_CAPTURE;
                Guid medType = MEDIATYPE_Video;
                int hr = capBuilder.RenderStream(ref pinCat, ref medType, _srcFilter, grabberFilter, nullRenderer);
                Marshal.ReleaseComObject(capBuilder);
                if (hr < 0) return false;

                var connMt = new AMMediaType();
                if (grabber.GetConnectedMediaType(connMt) == 0 && connMt.formatPtr != IntPtr.Zero)
                {
                    var vih = (VideoInfoHeader)Marshal.PtrToStructure(connMt.formatPtr, typeof(VideoInfoHeader));
                    Width = vih.BmiHeader.Width;
                    Height = Math.Abs(vih.BmiHeader.Height);
                }
                if (Width <= 0 || Height <= 0) { Width = 640; Height = 480; }

                _cb = new SampleGrabberCB(Width, Height, HandleRawFrame);
                grabber.SetBufferSamples(false);
                grabber.SetOneShot(false);
                grabber.SetCallback(_cb, 1);

                InitSensorDefaults();

                _mediaCtrl = (IMediaControl)graph;
                hr = _mediaCtrl.Run();
                return hr >= 0;
            }
            catch
            {
                return false;
            }
        }

        void HandleRawFrame(byte[] bgr24, int w, int h)
        {
            _tick++;
            if (_tick == 5 || _tick == 15 || _tick % 90 == 0)
                TuneSensor(bgr24, w, h);
            var handler = OnFrame;
            if (handler != null) handler(bgr24, w, h);
        }

        void InitSensorDefaults()
        {
            try
            {
                var amp = _srcFilter as IAMVideoProcAmp;
                if (amp == null) return;
                amp.Set(VideoProcAmpProperty.WhiteBalance, 0, VideoProcAmpFlags.Auto);
                amp.Set(VideoProcAmpProperty.BacklightCompensation, 2, VideoProcAmpFlags.Manual);
                int val, fl;
                if (amp.Get(VideoProcAmpProperty.Contrast, out val, out fl) == 0 && val >= 0 && val < 8)
                    amp.Set(VideoProcAmpProperty.Contrast, 12, VideoProcAmpFlags.Manual);
            }
            catch { }
        }

        void TuneSensor(byte[] bgr, int w, int h)
        {
            try
            {
                var amp = _srcFilter as IAMVideoProcAmp;
                var cam = _srcFilter as IAMCameraControl;
                if (amp == null) return;

                // Fast luminance histogram on 1/64 subsampled grid
                int[] hist = new int[256];
                int count = 0;
                for (int y = 0; y < h; y += 8)
                {
                    int row = y * w * 3;
                    for (int x = 0; x < w; x += 8)
                    {
                        int i = row + x * 3;
                        int lum = (bgr[i] * 29 + bgr[i + 1] * 150 + bgr[i + 2] * 77) >> 8;
                        hist[lum]++;
                        count++;
                    }
                }
                if (count == 0) return;

                int p25 = Percentile(hist, count, 25);
                int p92 = Percentile(hist, count, 92);

                int val, fl;
                if (p25 <= 8 && amp.Get(VideoProcAmpProperty.Brightness, out val, out fl) == 0 && val >= 0 && val < 54)
                    amp.Set(VideoProcAmpProperty.Brightness, Math.Min(56, val + 14), VideoProcAmpFlags.Manual);

                if (cam != null && p25 < 18 && p92 < 185)
                {
                    if (cam.Get(CameraControlProperty.Exposure, out val, out fl) == 0 && val >= -10 && val <= -6)
                        cam.Set(CameraControlProperty.Exposure, -5, CameraControlFlags.Manual);
                }
            }
            catch { }
        }

        static int Percentile(int[] hist, int total, int pct)
        {
            int target = (total * pct) / 100;
            int acc = 0;
            for (int i = 0; i < 256; i++)
            {
                acc += hist[i];
                if (acc >= target) return i;
            }
            return 255;
        }

        static IBaseFilter BindDeviceByIndex(int targetIndex)
        {
            ICreateDevEnum devEnum = null;
            IEnumMoniker enumMon = null;
            try
            {
                devEnum = (ICreateDevEnum)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_SystemDeviceEnum));
                Guid cat = CLSID_VideoInputDeviceCategory;
                if (devEnum.CreateClassEnumerator(ref cat, out enumMon, 0) != 0 || enumMon == null) return null;

                IMoniker[] mons = new IMoniker[1];
                int idx = 0;
                while (enumMon.Next(1, mons, IntPtr.Zero) == 0)
                {
                    if (idx == targetIndex)
                    {
                        Guid iid = typeof(IBaseFilter).GUID;
                        object obj;
                        mons[0].BindToObject(null, null, ref iid, out obj);
                        Marshal.ReleaseComObject(mons[0]);
                        return (IBaseFilter)obj;
                    }
                    Marshal.ReleaseComObject(mons[0]);
                    idx++;
                }
            }
            catch { }
            finally
            {
                if (enumMon != null) Marshal.ReleaseComObject(enumMon);
                if (devEnum != null) Marshal.ReleaseComObject(devEnum);
            }
            return null;
        }

        void CleanupGraph()
        {
            try { if (_mediaCtrl != null) _mediaCtrl.Stop(); } catch { }
            _mediaCtrl = null;
            if (_srcFilter != null) { try { Marshal.ReleaseComObject(_srcFilter); } catch { } _srcFilter = null; }
            if (_graphObj != null) { try { Marshal.ReleaseComObject(_graphObj); } catch { } _graphObj = null; }
        }

        public void Stop()
        {
            _running = false;
            if (_thread != null && _thread.IsAlive) _thread.Join(1200);
            CleanupGraph();
        }

        public void Dispose() { Stop(); }

        sealed class SampleGrabberCB : ISampleGrabberCB
        {
            readonly int _w, _h;
            readonly Action<byte[], int, int> _onFrame;
            byte[] _buf;
            public int LastTick = Environment.TickCount;

            public SampleGrabberCB(int w, int h, Action<byte[], int, int> onFrame)
            {
                _w = w;
                _h = h;
                _onFrame = onFrame;
                _buf = new byte[w * h * 3];
            }

            public int SampleCB(double sampleTime, IntPtr pSample) { return 0; }

            public unsafe int BufferCB(double sampleTime, IntPtr pBuffer, int bufferLen)
            {
                LastTick = Environment.TickCount;
                int need = _w * _h * 3;
                if (bufferLen < need) return 0;
                if (_buf.Length != need) _buf = new byte[need];

                // DirectShow RGB24 is bottom-up; flip vertically to top-down BGR24
                int stride = _w * 3;
                byte* src = (byte*)pBuffer;
                fixed (byte* dst = _buf)
                {
                    for (int y = 0; y < _h; y++)
                    {
                        Buffer.MemoryCopy(src + (_h - 1 - y) * stride, dst + y * stride, stride, stride);
                    }
                }
                _onFrame(_buf, _w, _h);
                return 0;
            }
        }

        #region DirectShow COM Definitions
        static readonly Guid CLSID_SystemDeviceEnum = new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
        static readonly Guid CLSID_VideoInputDeviceCategory = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
        static readonly Guid CLSID_FilterGraph = new Guid("e436ebb3-524f-11ce-9f53-0020af0ba770");
        static readonly Guid CLSID_CaptureGraphBuilder2 = new Guid("BF87B6E1-8C27-11d0-B3F0-00AA003761C5");
        static readonly Guid CLSID_SampleGrabber = new Guid("C1F400A0-3F08-11d3-9F0B-006008039E37");
        static readonly Guid CLSID_NullRenderer = new Guid("C1F400A4-3F08-11d3-9F0B-006008039E37");
        static readonly Guid PIN_CATEGORY_CAPTURE = new Guid("fb6c4281-0353-11d1-905f-0000c0cc16ba");
        static readonly Guid MEDIATYPE_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        static readonly Guid MEDIASUBTYPE_RGB24 = new Guid("e436eb7d-524f-11ce-9f53-0020af0ba770");
        static readonly Guid FORMAT_VideoInfo = new Guid("05589f80-c356-11ce-bf01-00aa0055595a");

        [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ICreateDevEnum
        {
            [PreserveSig]
            int CreateClassEnumerator([In] ref Guid pType, out IEnumMoniker ppEnumMoniker, int dwFlags);
        }

        [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyBag
        {
            [PreserveSig]
            int Read([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, out object pVar, object pErrorLog);
            [PreserveSig]
            int Write([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [In] ref object pVar);
        }

        [ComImport, Guid("56a86895-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IBaseFilter { }

        [ComImport, Guid("56a868a9-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IGraphBuilder
        {
            [PreserveSig] int AddFilter(IBaseFilter pFilter, [MarshalAs(UnmanagedType.LPWStr)] string pName);
        }

        [ComImport, Guid("93E5A4E0-2D50-11d2-ABFA-00A0C9C6E38D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ICaptureGraphBuilder2
        {
            [PreserveSig] int SetFiltergraph(IGraphBuilder pfg);
            [PreserveSig] int GetFiltergraph(out IGraphBuilder ppfg);
            [PreserveSig] int SetOutputFileName(ref Guid pType, [MarshalAs(UnmanagedType.LPWStr)] string lpstrFile, out IBaseFilter ppbf, out IntPtr ppSink);
            [PreserveSig] int FindInterface(ref Guid pCategory, ref Guid pType, IBaseFilter pf, ref Guid riid, out IntPtr ppint);
            [PreserveSig] int RenderStream(ref Guid pCategory, ref Guid pType, [MarshalAs(UnmanagedType.IUnknown)] object pSource, IBaseFilter pfCompressor, IBaseFilter pfRenderer);
        }

        [ComImport, Guid("56a868b1-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface IMediaControl
        {
            [PreserveSig] int Run();
            [PreserveSig] int Pause();
            [PreserveSig] int Stop();
        }

        [ComImport, Guid("6B652FFF-11FE-4fce-92AD-0266B5D7C78F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISampleGrabber
        {
            [PreserveSig] int SetOneShot(bool OneShot);
            [PreserveSig] int SetMediaType([In, MarshalAs(UnmanagedType.LPStruct)] AMMediaType pmt);
            [PreserveSig] int GetConnectedMediaType([Out, MarshalAs(UnmanagedType.LPStruct)] AMMediaType pmt);
            [PreserveSig] int SetBufferSamples(bool BufferThem);
            [PreserveSig] int GetCurrentBuffer(ref int pBufferSize, IntPtr pBuffer);
            [PreserveSig] int GetCurrentSample(out IntPtr ppSample);
            [PreserveSig] int SetCallback(ISampleGrabberCB pCallback, int WhichMethodToCallback);
        }

        [ComImport, Guid("0579154A-2B53-4994-B0D0-E773148EFF85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISampleGrabberCB
        {
            [PreserveSig] int SampleCB(double SampleTime, IntPtr pSample);
            [PreserveSig] int BufferCB(double SampleTime, IntPtr pBuffer, int BufferLen);
        }

        enum VideoProcAmpProperty { Brightness = 0, Contrast, Hue, Saturation, Sharpness, Gamma, ColorEnable, WhiteBalance, BacklightCompensation, Gain }
        [Flags] enum VideoProcAmpFlags { Auto = 1, Manual = 2 }

        [ComImport, Guid("C6E13360-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAMVideoProcAmp
        {
            [PreserveSig] int GetRange(VideoProcAmpProperty Property, out int pMin, out int pMax, out int pSteppingDelta, out int pDefault, out int pCapsFlags);
            [PreserveSig] int Set(VideoProcAmpProperty Property, int lValue, VideoProcAmpFlags Flags);
            [PreserveSig] int Get(VideoProcAmpProperty Property, out int lValue, out int Flags);
        }

        enum CameraControlProperty { Pan = 0, Tilt, Roll, Zoom, Exposure, Iris, Focus }
        [Flags] enum CameraControlFlags { Auto = 1, Manual = 2 }

        [ComImport, Guid("C6E13370-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAMCameraControl
        {
            [PreserveSig] int GetRange(CameraControlProperty Property, out int pMin, out int pMax, out int pSteppingDelta, out int pDefault, out int pCapsFlags);
            [PreserveSig] int Set(CameraControlProperty Property, int lValue, CameraControlFlags Flags);
            [PreserveSig] int Get(CameraControlProperty Property, out int lValue, out int Flags);
        }

        [StructLayout(LayoutKind.Sequential)]
        class AMMediaType
        {
            public Guid majorType;
            public Guid subType;
            [MarshalAs(UnmanagedType.Bool)] public bool fixedSizeSamples = true;
            [MarshalAs(UnmanagedType.Bool)] public bool temporalCompression;
            public int sampleSize = 1;
            public Guid formatType;
            public IntPtr unkPtr;
            public int formatSize;
            public IntPtr formatPtr;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VideoInfoHeader
        {
            public RECT SrcRect;
            public RECT TargetRect;
            public int BitRate;
            public int BitErrorRate;
            public long AvgTimePerFrame;
            public BitmapInfoHeader BmiHeader;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct BitmapInfoHeader
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public int ClrUsed;
            public int ClrImportant;
        }
        #endregion
    }
}
