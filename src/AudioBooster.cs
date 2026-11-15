using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace AlapCamera
{
    internal sealed class AudioDeviceInfo
    {
        public int Id;
        public string Name;
        public override string ToString() { return Name; }
    }

    internal sealed class AudioBooster : IDisposable
    {
        const int SampleRate = 48000;
        const int Channels = 1;
        const int BitsPerSample = 16;
        const int BufferMs = 20;
        const int BufferBytes = (SampleRate * Channels * (BitsPerSample / 8) * BufferMs) / 1000;
        const int NumBuffers = 4;

        const uint CALLBACK_NULL = 0x00000000;
        const uint CALLBACK_EVENT = 0x00050000;
        const uint WHDR_DONE = 0x00000001;
        const uint WHDR_INQUEUE = 0x00000010;
        const uint WIM_DATA = 0x3C0;
        const uint WOM_DONE = 0x3BD;
        const uint DRV_QUERYFUNCTIONINSTANCEID = 0x0811;
        const uint DRV_QUERYFUNCTIONINSTANCEIDSIZE = 0x0812;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WAVEINCAPSW
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WAVEOUTCAPSW
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
            public uint dwSupport;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        delegate void WaveCallback(IntPtr hWave, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [DllImport("winmm.dll")] static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int waveInGetDevCapsW(UIntPtr uDeviceID, out WAVEINCAPSW pwic, uint cbwic);
        [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
        [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] static extern int waveInStart(IntPtr hwi);
        [DllImport("winmm.dll")] static extern int waveInReset(IntPtr hwi);
        [DllImport("winmm.dll")] static extern int waveInClose(IntPtr hwi);
        [DllImport("winmm.dll")] static extern int waveInMessage(IntPtr hwi, uint uMsg, IntPtr dw1, IntPtr dw2);

        [DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int waveOutGetDevCapsW(UIntPtr uDeviceID, out WAVEOUTCAPSW pwoc, uint cbwoc);
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr phwo, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutMessage(IntPtr hwo, uint uMsg, IntPtr dw1, IntPtr dw2);

        public volatile int VolumePercent = 100; // 0..200
        public bool IsRunning { get; private set; }
        public string LastError { get; private set; }

        readonly object _lock = new object();
        IntPtr _hIn;
        IntPtr _hOut;
        AutoResetEvent _inEvent;
        Thread _worker;

        IntPtr[] _inHdrs;
        IntPtr[] _inBufs;
        IntPtr[] _outHdrs;
        IntPtr[] _outBufs;
        int _outIdx;
        volatile bool _stopping;

        public static List<AudioDeviceInfo> ListInputs()
        {
            var list = new List<AudioDeviceInfo>();
            uint n = waveInGetNumDevs();
            for (uint i = 0; i < n; i++)
            {
                WAVEINCAPSW caps;
                if (waveInGetDevCapsW((UIntPtr)i, out caps, (uint)Marshal.SizeOf(typeof(WAVEINCAPSW))) == 0)
                    list.Add(new AudioDeviceInfo { Id = (int)i, Name = GetFullDeviceName(i, true, caps.szPname) });
            }
            return list;
        }

        public static List<AudioDeviceInfo> ListOutputs()
        {
            var list = new List<AudioDeviceInfo>();
            uint n = waveOutGetNumDevs();
            for (uint i = 0; i < n; i++)
            {
                WAVEOUTCAPSW caps;
                if (waveOutGetDevCapsW((UIntPtr)i, out caps, (uint)Marshal.SizeOf(typeof(WAVEOUTCAPSW))) == 0)
                    list.Add(new AudioDeviceInfo { Id = (int)i, Name = GetFullDeviceName(i, false, caps.szPname) });
            }
            return list;
        }

        static string GetFullDeviceName(uint devId, bool isInput, string fallback)
        {
            try
            {
                IntPtr pSize = Marshal.AllocHGlobal(4);
                try
                {
                    Marshal.WriteInt32(pSize, 0);
                    int rc = isInput
                        ? waveInMessage((IntPtr)devId, DRV_QUERYFUNCTIONINSTANCEIDSIZE, pSize, IntPtr.Zero)
                        : waveOutMessage((IntPtr)devId, DRV_QUERYFUNCTIONINSTANCEIDSIZE, pSize, IntPtr.Zero);
                    int bytes = Marshal.ReadInt32(pSize);
                    if (rc == 0 && bytes > 4 && bytes < 1024)
                    {
                        IntPtr pStr = Marshal.AllocHGlobal(bytes);
                        try
                        {
                            rc = isInput
                                ? waveInMessage((IntPtr)devId, DRV_QUERYFUNCTIONINSTANCEID, pStr, (IntPtr)bytes)
                                : waveOutMessage((IntPtr)devId, DRV_QUERYFUNCTIONINSTANCEID, pStr, (IntPtr)bytes);
                            if (rc == 0)
                            {
                                string endpointId = Marshal.PtrToStringUni(pStr) ?? "";
                                int dot = endpointId.LastIndexOf('.');
                                string guid = dot >= 0 ? endpointId.Substring(dot + 1) : endpointId;
                                if (!string.IsNullOrEmpty(guid))
                                {
                                    string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\" + (isInput ? "Capture" : "Render") + "\\" + guid + @"\Properties";
                                    using (var k = Registry.LocalMachine.OpenSubKey(sub))
                                    {
                                        if (k != null)
                                        {
                                            string devDesc = k.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
                                            string iface = (k.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string)
                                                ?? (k.GetValue("{026e516e-b814-414b-83cd-856d6fef4822},2") as string);
                                            if (!string.IsNullOrEmpty(devDesc) && !string.IsNullOrEmpty(iface))
                                                return devDesc + " (" + iface + ")";
                                            if (!string.IsNullOrEmpty(devDesc)) return devDesc;
                                        }
                                    }
                                }
                            }
                        }
                        finally { Marshal.FreeHGlobal(pStr); }
                    }
                }
                finally { Marshal.FreeHGlobal(pSize); }
            }
            catch { }
            return fallback;
        }

        public bool Start(int inDevId, int outDevId)
        {
            Stop();
            LastError = null;
            lock (_lock)
            {
                try
                {
                    _stopping = false;
                    var fmt = new WAVEFORMATEX
                    {
                        wFormatTag = 1, // PCM
                        nChannels = Channels,
                        nSamplesPerSec = SampleRate,
                        wBitsPerSample = BitsPerSample,
                        nBlockAlign = (ushort)(Channels * (BitsPerSample / 8)),
                        nAvgBytesPerSec = (uint)(SampleRate * Channels * (BitsPerSample / 8)),
                        cbSize = 0
                    };

                    int rc = waveOutOpen(out _hOut, (uint)outDevId, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
                    if (rc != 0 || _hOut == IntPtr.Zero)
                    {
                        LastError = "waveOutOpen error " + rc;
                        Stop();
                        return false;
                    }

                    _inEvent = new AutoResetEvent(false);
                    IntPtr hEvt = _inEvent.SafeWaitHandle.DangerousGetHandle();
                    rc = waveInOpen(out _hIn, (uint)inDevId, ref fmt, hEvt, IntPtr.Zero, CALLBACK_EVENT);
                    if (rc != 0 || _hIn == IntPtr.Zero)
                    {
                        LastError = "waveInOpen error " + rc;
                        Stop();
                        return false;
                    }

                    int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
                    _inHdrs = new IntPtr[NumBuffers];
                    _inBufs = new IntPtr[NumBuffers];
                    _outHdrs = new IntPtr[NumBuffers];
                    _outBufs = new IntPtr[NumBuffers];

                    for (int i = 0; i < NumBuffers; i++)
                    {
                        _outBufs[i] = Marshal.AllocHGlobal(BufferBytes);
                        _outHdrs[i] = Marshal.AllocHGlobal(hdrSize);
                        var oh = new WAVEHDR { lpData = _outBufs[i], dwBufferLength = BufferBytes };
                        Marshal.StructureToPtr(oh, _outHdrs[i], false);
                        waveOutPrepareHeader(_hOut, _outHdrs[i], (uint)hdrSize);

                        _inBufs[i] = Marshal.AllocHGlobal(BufferBytes);
                        _inHdrs[i] = Marshal.AllocHGlobal(hdrSize);
                        var ih = new WAVEHDR { lpData = _inBufs[i], dwBufferLength = BufferBytes };
                        Marshal.StructureToPtr(ih, _inHdrs[i], false);
                        waveInPrepareHeader(_hIn, _inHdrs[i], (uint)hdrSize);
                        waveInAddBuffer(_hIn, _inHdrs[i], (uint)hdrSize);
                    }

                    _outIdx = 0;
                    IsRunning = true;
                    _worker = new Thread(AudioLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "AlapAudio" };
                    _worker.Start();
                    waveInStart(_hIn);
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Stop();
                    return false;
                }
            }
        }

        unsafe void AudioLoop()
        {
            while (!_stopping && IsRunning)
            {
                try
                {
                    var evt = _inEvent;
                    if (evt == null) break;
                    evt.WaitOne(100);
                    if (_stopping || !IsRunning || _inHdrs == null || _outHdrs == null) break;

                    for (int b = 0; b < NumBuffers; b++)
                    {
                        if (_stopping || _hIn == IntPtr.Zero || _hOut == IntPtr.Zero) break;
                        var inHdr = (WAVEHDR*)_inHdrs[b];
                        if ((inHdr->dwFlags & WHDR_DONE) == 0) continue;

                        int bytes = (int)inHdr->dwBytesRecorded;
                        if (bytes > 0)
                        {
                            int idx = _outIdx++ % NumBuffers;
                            var outHdr = (WAVEHDR*)_outHdrs[idx];
                            if ((outHdr->dwFlags & WHDR_INQUEUE) == 0)
                            {
                                short* src = (short*)inHdr->lpData;
                                short* dst = (short*)outHdr->lpData;
                                int samples = Math.Min(bytes, BufferBytes) >> 1;
                                float gain = VolumePercent / 100.0f;

                                for (int i = 0; i < samples; i++)
                                {
                                    float s = (src[i] / 32768.0f) * gain;
                                    if (s > 0.85f)
                                    {
                                        float ex = s - 0.85f;
                                        s = 0.85f + 0.14f * (ex / (1.0f + ex * 3.0f));
                                    }
                                    else if (s < -0.85f)
                                    {
                                        float ex = -0.85f - s;
                                        s = -(0.85f + 0.14f * (ex / (1.0f + ex * 3.0f)));
                                    }
                                    int v = (int)(s * 32767.0f);
                                    if (v > 32767) v = 32767;
                                    else if (v < -32768) v = -32768;
                                    dst[i] = (short)v;
                                }

                                outHdr->dwBufferLength = (uint)(samples << 1);
                                waveOutWrite(_hOut, _outHdrs[idx], (uint)sizeof(WAVEHDR));
                            }
                        }

                        if (!_stopping && _hIn != IntPtr.Zero)
                            waveInAddBuffer(_hIn, _inHdrs[b], (uint)sizeof(WAVEHDR));
                    }
                }
                catch { }
            }
        }

        public void Stop()
        {
            _stopping = true;
            IsRunning = false;
            try { if (_inEvent != null) _inEvent.Set(); } catch { }
            var w = _worker;
            _worker = null;
            if (w != null && w != Thread.CurrentThread)
            {
                try { w.Join(300); } catch { }
            }

            lock (_lock)
            {
                int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
                if (_hIn != IntPtr.Zero)
                {
                    try { waveInReset(_hIn); } catch { }
                    if (_inHdrs != null)
                    {
                        for (int i = 0; i < _inHdrs.Length; i++)
                        {
                            if (_inHdrs[i] != IntPtr.Zero)
                            {
                                try { waveInUnprepareHeader(_hIn, _inHdrs[i], (uint)hdrSize); } catch { }
                                Marshal.FreeHGlobal(_inHdrs[i]);
                            }
                            if (_inBufs != null && _inBufs[i] != IntPtr.Zero) Marshal.FreeHGlobal(_inBufs[i]);
                        }
                    }
                    try { waveInClose(_hIn); } catch { }
                    _hIn = IntPtr.Zero;
                    _inHdrs = null;
                    _inBufs = null;
                }

                if (_hOut != IntPtr.Zero)
                {
                    try { waveOutReset(_hOut); } catch { }
                    if (_outHdrs != null)
                    {
                        for (int i = 0; i < _outHdrs.Length; i++)
                        {
                            if (_outHdrs[i] != IntPtr.Zero)
                            {
                                try { waveOutUnprepareHeader(_hOut, _outHdrs[i], (uint)hdrSize); } catch { }
                                Marshal.FreeHGlobal(_outHdrs[i]);
                            }
                            if (_outBufs != null && _outBufs[i] != IntPtr.Zero) Marshal.FreeHGlobal(_outBufs[i]);
                        }
                    }
                    try { waveOutClose(_hOut); } catch { }
                    _hOut = IntPtr.Zero;
                    _outHdrs = null;
                    _outBufs = null;
                }

                if (_inEvent != null)
                {
                    try { _inEvent.Close(); } catch { }
                    _inEvent = null;
                }
            }
        }

        public void Dispose() { Stop(); }
    }
}
