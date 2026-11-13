using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AlapCamera
{
    internal sealed class OnnxSeg : IDisposable
    {
        public const int ModelWidth = 256;
        public const int ModelHeight = 256;
        private const string ResourceName = "AlapCamera.Models.selfie_segmentation.onnx";
        private const uint ORT_API_VERSION = 17;

        public bool IsAvailable { get; private set; }
        public string StatusText { get; private set; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpLibFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr FnOrtGetApiBase();

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr FnGetApi(uint version);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr FnGetErrorMessage(IntPtr status);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr FnCreateEnv(int logLevel, IntPtr logId, out IntPtr env);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr FnCreateSessionOptions(out IntPtr options);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr FnSetIntraOpNumThreads(IntPtr options, int intraOpNumThreads);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr FnCreateSessionFromArray(IntPtr env, IntPtr modelData, UIntPtr modelDataLen, IntPtr options, out IntPtr session);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr FnCreateCpuMemoryInfo(int allocType, int memType, out IntPtr memInfo);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr FnCreateTensorWithDataAsOrtValue(
            IntPtr info, IntPtr pData, UIntPtr pDataLen,
            long[] shape, UIntPtr shapeLen, int type, out IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr FnRun(
            IntPtr session, IntPtr runOptions,
            IntPtr[] inputNames, IntPtr[] inputs, UIntPtr inputLen,
            IntPtr[] outputNames, UIntPtr outputLen, IntPtr[] outputs);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr FnGetTensorMutableData(IntPtr value, out IntPtr pData);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void FnRelease(IntPtr ptr);

        private IntPtr _hDll;
        private IntPtr _env;
        private IntPtr _opts;
        private IntPtr _session;
        private IntPtr _memInfo;
        private IntPtr _inputTensor;

        private FnGetErrorMessage _getErr;
        private FnRun _run;
        private FnGetTensorMutableData _getData;
        private FnRelease _relEnv;
        private FnRelease _relStatus;
        private FnRelease _relMem;
        private FnRelease _relSession;
        private FnRelease _relValue;
        private FnRelease _relOpts;

        private readonly float[] _nchw = new float[3 * ModelWidth * ModelHeight];
        private GCHandle _nchwPin;
        private IntPtr _inNamePtr;
        private IntPtr _outNamePtr;
        private IntPtr[] _inNames;
        private IntPtr[] _outNames;
        private IntPtr[] _inVals;
        private IntPtr[] _outVals = new IntPtr[1];

        private readonly object _lock = new object();
        private bool _disposed;

        public OnnxSeg()
        {
            try
            {
                Init();
            }
            catch (Exception ex)
            {
                IsAvailable = false;
                StatusText = "AI fallback (" + ex.Message + ")";
                Dispose();
            }
        }

        private static T GetSlot<T>(IntPtr api, int index) where T : class
        {
            IntPtr fnPtr = Marshal.ReadIntPtr(api, index * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(fnPtr, typeof(T)) as T;
        }

        private void CheckStatus(IntPtr status)
        {
            if (status == IntPtr.Zero) return;
            string msg = "ORT error";
            try
            {
                IntPtr p = _getErr(status);
                if (p != IntPtr.Zero) msg = Marshal.PtrToStringAnsi(p) ?? msg;
            }
            finally
            {
                if (_relStatus != null) _relStatus(status);
            }
            throw new InvalidOperationException(msg);
        }

        private void Init()
        {
            string sysOrt = Path.Combine(Environment.SystemDirectory, "onnxruntime.dll");
            if (!File.Exists(sysOrt))
            {
                StatusText = "onnxruntime.dll not found";
                return;
            }

            _hDll = LoadLibraryW(sysOrt);
            if (_hDll == IntPtr.Zero)
            {
                StatusText = "LoadLibrary onnxruntime.dll failed";
                return;
            }

            IntPtr pBase = GetProcAddress(_hDll, "OrtGetApiBase");
            if (pBase == IntPtr.Zero)
            {
                StatusText = "OrtGetApiBase missing";
                return;
            }

            var getBase = (FnOrtGetApiBase)Marshal.GetDelegateForFunctionPointer(pBase, typeof(FnOrtGetApiBase));
            IntPtr apiBase = getBase();
            IntPtr pGetApi = Marshal.ReadIntPtr(apiBase, 0);
            var getApi = (FnGetApi)Marshal.GetDelegateForFunctionPointer(pGetApi, typeof(FnGetApi));
            IntPtr api = getApi(ORT_API_VERSION);
            if (api == IntPtr.Zero)
            {
                StatusText = "ORT API v17 unavailable";
                return;
            }

            _getErr = GetSlot<FnGetErrorMessage>(api, 2);
            var createEnv = GetSlot<FnCreateEnv>(api, 3);
            var createFromArray = GetSlot<FnCreateSessionFromArray>(api, 8);
            _run = GetSlot<FnRun>(api, 9);
            var createOpts = GetSlot<FnCreateSessionOptions>(api, 10);
            var setThreads = GetSlot<FnSetIntraOpNumThreads>(api, 24);
            var createTensor = GetSlot<FnCreateTensorWithDataAsOrtValue>(api, 49);
            _getData = GetSlot<FnGetTensorMutableData>(api, 51);
            var createMem = GetSlot<FnCreateCpuMemoryInfo>(api, 69);

            _relEnv = GetSlot<FnRelease>(api, 92);
            _relStatus = GetSlot<FnRelease>(api, 93);
            _relMem = GetSlot<FnRelease>(api, 94);
            _relSession = GetSlot<FnRelease>(api, 95);
            _relValue = GetSlot<FnRelease>(api, 96);
            _relOpts = GetSlot<FnRelease>(api, 100);

            byte[] modelBytes;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (s == null)
                {
                    StatusText = "Embedded AI model missing";
                    return;
                }
                modelBytes = new byte[s.Length];
                int read = 0;
                while (read < modelBytes.Length)
                {
                    int n = s.Read(modelBytes, read, modelBytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
            }

            IntPtr logId = Marshal.StringToHGlobalAnsi("AlapCamera");
            try
            {
                CheckStatus(createEnv(3, logId, out _env));
            }
            finally
            {
                Marshal.FreeHGlobal(logId);
            }

            CheckStatus(createOpts(out _opts));
            CheckStatus(setThreads(_opts, 4));

            IntPtr modelPtr = Marshal.AllocHGlobal(modelBytes.Length);
            try
            {
                Marshal.Copy(modelBytes, 0, modelPtr, modelBytes.Length);
                CheckStatus(createFromArray(_env, modelPtr, (UIntPtr)modelBytes.Length, _opts, out _session));
            }
            finally
            {
                Marshal.FreeHGlobal(modelPtr);
            }

            CheckStatus(createMem(0, 0, out _memInfo));

            _nchwPin = GCHandle.Alloc(_nchw, GCHandleType.Pinned);
            long[] shape = new long[] { 1, 3, ModelHeight, ModelWidth };
            CheckStatus(createTensor(
                _memInfo,
                _nchwPin.AddrOfPinnedObject(),
                (UIntPtr)(_nchw.Length * sizeof(float)),
                shape,
                (UIntPtr)4,
                1, // ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT
                out _inputTensor));

            _inNamePtr = Marshal.StringToHGlobalAnsi("pixel_values");
            _outNamePtr = Marshal.StringToHGlobalAnsi("alphas");
            _inNames = new[] { _inNamePtr };
            _outNames = new[] { _outNamePtr };
            _inVals = new[] { _inputTensor };

            IsAvailable = true;
            StatusText = "AI Selfie Segmentation (ORT 1.17)";
        }

        public bool Infer(byte[] bgr, int w, int h, float[] outMask256)
        {
            if (!IsAvailable || _disposed || outMask256 == null || outMask256.Length < ModelWidth * ModelHeight)
                return false;

            lock (_lock)
            {
                if (!IsAvailable || _disposed) return false;

                int plane = ModelWidth * ModelHeight;
                const float inv255 = 1.0f / 255.0f;
                int stride = w * 3;

                for (int y = 0; y < ModelHeight; y++)
                {
                    int sy = (y * h) / ModelHeight;
                    int rowOff = sy * stride;
                    int dstRow = y * ModelWidth;
                    for (int x = 0; x < ModelWidth; x++)
                    {
                        int sx = (x * w) / ModelWidth;
                        int px = rowOff + sx * 3;
                        int idx = dstRow + x;
                        _nchw[idx] = bgr[px + 2] * inv255;              // R
                        _nchw[plane + idx] = bgr[px + 1] * inv255;      // G
                        _nchw[plane * 2 + idx] = bgr[px] * inv255;      // B
                    }
                }

                _outVals[0] = IntPtr.Zero;
                IntPtr st = _run(_session, IntPtr.Zero, _inNames, _inVals, (UIntPtr)1, _outNames, (UIntPtr)1, _outVals);
                if (st != IntPtr.Zero)
                {
                    if (_relStatus != null) _relStatus(st);
                    if (_outVals[0] != IntPtr.Zero && _relValue != null) _relValue(_outVals[0]);
                    return false;
                }

                try
                {
                    IntPtr outPtr;
                    st = _getData(_outVals[0], out outPtr);
                    if (st != IntPtr.Zero)
                    {
                        if (_relStatus != null) _relStatus(st);
                        return false;
                    }
                    Marshal.Copy(outPtr, outMask256, 0, plane);
                    return true;
                }
                finally
                {
                    if (_outVals[0] != IntPtr.Zero && _relValue != null)
                        _relValue(_outVals[0]);
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                IsAvailable = false;

                if (_inputTensor != IntPtr.Zero && _relValue != null) { _relValue(_inputTensor); _inputTensor = IntPtr.Zero; }
                if (_memInfo != IntPtr.Zero && _relMem != null) { _relMem(_memInfo); _memInfo = IntPtr.Zero; }
                if (_session != IntPtr.Zero && _relSession != null) { _relSession(_session); _session = IntPtr.Zero; }
                if (_opts != IntPtr.Zero && _relOpts != null) { _relOpts(_opts); _opts = IntPtr.Zero; }
                if (_env != IntPtr.Zero && _relEnv != null) { _relEnv(_env); _env = IntPtr.Zero; }
                if (_nchwPin.IsAllocated) _nchwPin.Free();
                if (_inNamePtr != IntPtr.Zero) { Marshal.FreeHGlobal(_inNamePtr); _inNamePtr = IntPtr.Zero; }
                if (_outNamePtr != IntPtr.Zero) { Marshal.FreeHGlobal(_outNamePtr); _outNamePtr = IntPtr.Zero; }
            }
        }
    }
}
