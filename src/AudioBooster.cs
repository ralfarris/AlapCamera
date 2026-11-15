using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AlapCamera
{
    internal sealed class AudioDeviceInfo
    {
        public int Id;
        public string EndpointId;
        public string Name;
        public override string ToString() { return Name; }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        int GetCount(out uint pcDevices);
        int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(int stgmAccess, IntPtr ppProperties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out uint pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        int GetMasterVolumeLevel(out float pfLevelDB);
        int GetMasterVolumeLevelScalar(out float pfLevel);
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
        int VolumeStepUp(ref Guid pguidEventContext);
        int VolumeStepDown(out Guid pguidEventContext);
        int QueryHardwareSupport(out uint pdwHardwareSupportMask);
        int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
    }

    internal sealed class AudioBooster : IDisposable
    {
        int _volumePercent = 140;
        string _activeEndpointId;
        float _savedScalar = -1f;

        public int VolumePercent
        {
            get { return _volumePercent; }
            set
            {
                _volumePercent = Math.Max(0, Math.Min(200, value));
                if (IsRunning && !string.IsNullOrEmpty(_activeEndpointId))
                    ApplyEndpointVolume(_activeEndpointId, _volumePercent, false);
            }
        }

        public bool IsRunning { get; private set; }
        public string LastError { get; private set; }

        public static List<AudioDeviceInfo> ListInputs()
        {
            var list = new List<AudioDeviceInfo>();
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                IMMDeviceCollection col;
                if (en.EnumAudioEndpoints(1, 1, out col) == 0 && col != null) // eCapture = 1, ACTIVE = 1
                {
                    uint count;
                    col.GetCount(out count);
                    for (uint i = 0; i < count; i++)
                    {
                        IMMDevice dev;
                        if (col.Item(i, out dev) == 0 && dev != null)
                        {
                            string id;
                            dev.GetId(out id);
                            string name = GetEndpointFriendlyName(id);
                            list.Add(new AudioDeviceInfo { Id = (int)i, EndpointId = id, Name = name });
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        static string GetEndpointFriendlyName(string endpointId)
        {
            try
            {
                int dot = endpointId != null ? endpointId.LastIndexOf('.') : -1;
                string guid = dot >= 0 ? endpointId.Substring(dot + 1) : endpointId;
                if (!string.IsNullOrEmpty(guid))
                {
                    string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\" + guid + @"\Properties";
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
            catch { }
            return endpointId ?? "Microphone";
        }

        public bool Start(string endpointId)
        {
            Stop();
            LastError = null;
            if (string.IsNullOrEmpty(endpointId))
            {
                LastError = "Mic tidak valid";
                return false;
            }
            if (ApplyEndpointVolume(endpointId, _volumePercent, true))
            {
                _activeEndpointId = endpointId;
                IsRunning = true;
                return true;
            }
            return false;
        }

        bool ApplyEndpointVolume(string endpointId, int percent, bool saveOriginal)
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                IMMDevice dev;
                if (en.GetDevice(endpointId, out dev) != 0 || dev == null)
                {
                    LastError = "Device tidak ditemukan";
                    return false;
                }
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                if (dev.Activate(ref iid, 1, IntPtr.Zero, out o) != 0 || o == null)
                {
                    LastError = "Gagal akses kontrol volume mic";
                    return false;
                }
                var ep = (IAudioEndpointVolume)o;
                if (saveOriginal)
                {
                    float cur;
                    if (ep.GetMasterVolumeLevelScalar(out cur) == 0) _savedScalar = cur;
                }

                Guid ctx = Guid.Empty;
                if (percent <= 0)
                {
                    ep.SetMute(true, ref ctx);
                    ep.SetMasterVolumeLevelScalar(0f, ref ctx);
                }
                else
                {
                    ep.SetMute(false, ref ctx);
                    // Map 0..200% -> 0.0 .. 1.0 hardware scalar (where 200% unlocks full +30 dB hardware gain)
                    float scalar = Math.Max(0f, Math.Min(1f, percent / 200f));
                    ep.SetMasterVolumeLevelScalar(scalar, ref ctx);
                }
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public void Stop()
        {
            if (IsRunning && !string.IsNullOrEmpty(_activeEndpointId) && _savedScalar >= 0f)
            {
                try
                {
                    var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                    IMMDevice dev;
                    if (en.GetDevice(_activeEndpointId, out dev) == 0 && dev != null)
                    {
                        Guid iid = typeof(IAudioEndpointVolume).GUID;
                        object o;
                        if (dev.Activate(ref iid, 1, IntPtr.Zero, out o) == 0 && o != null)
                        {
                            var ep = (IAudioEndpointVolume)o;
                            Guid ctx = Guid.Empty;
                            ep.SetMute(false, ref ctx);
                            ep.SetMasterVolumeLevelScalar(_savedScalar, ref ctx);
                        }
                    }
                }
                catch { }
            }
            _activeEndpointId = null;
            _savedScalar = -1f;
            IsRunning = false;
        }

        public void Dispose()
        {
            // Keep boosted volume active even when closing app if enabled, or do nothing
            _activeEndpointId = null;
            IsRunning = false;
        }
    }
}
