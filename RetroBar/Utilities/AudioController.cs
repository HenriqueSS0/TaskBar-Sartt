using ManagedShell.Common.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Volume, mute and output device selection through Windows Core Audio.
    /// </summary>
    public sealed class AudioController : IDisposable
    {
        public sealed class OutputDevice
        {
            public string Id { get; init; }
            public string Name { get; init; }
            public bool IsDefault { get; init; }
        }

        /// <summary>
        /// One app in the volume mixer. Apps with several audio sessions (e.g. browsers) are
        /// grouped, and volume/mute apply to all of their sessions.
        /// </summary>
        public sealed class AppAudio
        {
            internal readonly List<ISimpleAudioVolume> Volumes = new List<ISimpleAudioVolume>();
            public string Name { get; internal set; }
            public string ExecutablePath { get; internal set; }
            public bool IsSystemSounds { get; internal set; }
            public int ProcessId { get; internal set; }

            public float Volume
            {
                get
                {
                    if (Volumes.Count == 0) return 0;
                    Volumes[0].GetMasterVolume(out float level);
                    return level;
                }
                set
                {
                    Guid context = Guid.Empty;
                    foreach (var volume in Volumes)
                    {
                        volume.SetMasterVolume(Math.Clamp(value, 0f, 1f), ref context);
                    }
                }
            }

            public bool IsMuted
            {
                get
                {
                    if (Volumes.Count == 0) return false;
                    Volumes[0].GetMute(out bool muted);
                    return muted;
                }
                set
                {
                    Guid context = Guid.Empty;
                    foreach (var volume in Volumes)
                    {
                        volume.SetMute(value, ref context);
                    }
                }
            }

            internal void Release()
            {
                foreach (var volume in Volumes)
                {
                    Marshal.ReleaseComObject(volume);
                }
                Volumes.Clear();
            }
        }

        private readonly List<AppAudio> _apps = new List<AppAudio>();

        /// <summary>
        /// Apps playing audio on any active output device (an app may use a device other than
        /// the default, e.g. a headset). Sessions of the same app are merged.
        /// </summary>
        public List<AppAudio> GetApps()
        {
            foreach (var app in _apps)
            {
                app.Release();
            }
            _apps.Clear();

            if (_enumerator == null ||
                _enumerator.EnumAudioEndpoints(EDataFlow.Render, DEVICE_STATE_ACTIVE, out IMMDeviceCollection devices) != 0 ||
                devices == null)
            {
                return _apps;
            }

            var byKey = new Dictionary<string, AppAudio>(StringComparer.OrdinalIgnoreCase);
            devices.GetCount(out int deviceCount);
            for (int d = 0; d < deviceCount; d++)
            {
                if (devices.Item(d, out IMMDevice device) == 0 && device != null)
                {
                    CollectSessions(device, byKey);
                    Marshal.ReleaseComObject(device);
                }
            }
            Marshal.ReleaseComObject(devices);

            // System sounds last, apps alphabetically, like the Windows mixer.
            _apps.Sort((a, b) => a.IsSystemSounds != b.IsSystemSounds
                ? (a.IsSystemSounds ? 1 : -1)
                : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return _apps;
        }

        private void CollectSessions(IMMDevice device, Dictionary<string, AppAudio> byKey)
        {
            try
            {
                Guid iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object managerObject) != 0 ||
                    managerObject is not IAudioSessionManager2 manager)
                {
                    return;
                }

                if (manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions) == 0 && sessions != null)
                {
                    sessions.GetCount(out int count);

                    for (int i = 0; i < count; i++)
                    {
                        if (sessions.GetSession(i, out IAudioSessionControl2 session) != 0 || session == null)
                        {
                            continue;
                        }

                        session.GetState(out int state);
                        if (state == AudioSessionStateExpired)
                        {
                            Marshal.ReleaseComObject(session);
                            continue;
                        }

                        bool isSystem = session.IsSystemSoundsSession() == 0;
                        session.GetProcessId(out int pid);

                        string path = null;
                        string name = null;
                        if (isSystem)
                        {
                            name = "Sons do sistema";
                        }
                        else
                        {
                            try
                            {
                                using var process = System.Diagnostics.Process.GetProcessById(pid);
                                try
                                {
                                    path = process.MainModule?.FileName;
                                    name = process.MainModule?.FileVersionInfo.FileDescription;
                                }
                                catch
                                {
                                    // Some processes (elevated, protected) don't expose their module.
                                }

                                if (string.IsNullOrWhiteSpace(name))
                                {
                                    name = process.ProcessName;
                                }
                            }
                            catch
                            {
                                // The process already exited.
                                Marshal.ReleaseComObject(session);
                                continue;
                            }
                        }

                        string key = isSystem ? "::system" : (path ?? name);
                        if (!byKey.TryGetValue(key, out AppAudio app))
                        {
                            app = new AppAudio { Name = name, ExecutablePath = path, IsSystemSounds = isSystem, ProcessId = pid };
                            byKey[key] = app;
                            _apps.Add(app);
                        }

                        ISimpleAudioVolume simpleVolume = QuerySimpleVolume(session);
                        if (simpleVolume != null)
                        {
                            app.Volumes.Add(simpleVolume);
                        }
                        Marshal.ReleaseComObject(session);
                    }

                    Marshal.ReleaseComObject(sessions);
                }

                Marshal.ReleaseComObject(manager);
            }
            catch (Exception e)
            {
                ShellLogger.Error($"AudioController: Unable to list app sessions: {e.Message}");
            }
        }

        public static string LastQueryError { get; private set; }

        // Each session object also implements ISimpleAudioVolume (that's how Windows' own mixer
        // sets per-app volume). Query it explicitly through the session's IUnknown.
        private static ISimpleAudioVolume QuerySimpleVolume(object session)
        {
            IntPtr unknown = IntPtr.Zero;
            IntPtr volumePointer = IntPtr.Zero;
            try
            {
                unknown = Marshal.GetIUnknownForObject(session);
                Guid iid = typeof(ISimpleAudioVolume).GUID;
                int hr = Marshal.QueryInterface(unknown, in iid, out volumePointer);
                if (hr != 0 || volumePointer == IntPtr.Zero)
                {
                    LastQueryError = $"QueryInterface(ISimpleAudioVolume) hr=0x{hr:X8}";
                    return null;
                }

                return (ISimpleAudioVolume)Marshal.GetUniqueObjectForIUnknown(volumePointer);
            }
            catch (Exception e)
            {
                LastQueryError = e.GetType().Name + ": " + e.Message;
                return null;
            }
            finally
            {
                if (volumePointer != IntPtr.Zero) Marshal.Release(volumePointer);
                if (unknown != IntPtr.Zero) Marshal.Release(unknown);
            }
        }

        private IMMDeviceEnumerator _enumerator;
        private IAudioEndpointVolume _volume;
        private Guid _eventContext = Guid.Empty;

        public AudioController()
        {
            try
            {
                _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                RefreshDefaultDevice();
            }
            catch (Exception e)
            {
                ShellLogger.Error($"AudioController: Unable to initialize: {e.Message}");
            }
        }

        public bool IsAvailable => _volume != null;

        public void RefreshDefaultDevice()
        {
            ReleaseVolume();

            if (_enumerator == null ||
                _enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out IMMDevice device) != 0 ||
                device == null)
            {
                return;
            }

            Guid iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object volume) == 0)
            {
                _volume = volume as IAudioEndpointVolume;
            }

            Marshal.ReleaseComObject(device);
        }

        /// <summary>Master volume, 0 to 1.</summary>
        public float Volume
        {
            get
            {
                if (_volume == null) return 0;
                _volume.GetMasterVolumeLevelScalar(out float level);
                return level;
            }
            set
            {
                _volume?.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref _eventContext);
            }
        }

        public bool IsMuted
        {
            get
            {
                if (_volume == null) return false;
                _volume.GetMute(out bool muted);
                return muted;
            }
            set
            {
                _volume?.SetMute(value, ref _eventContext);
            }
        }

        public List<OutputDevice> GetOutputDevices()
        {
            var result = new List<OutputDevice>();
            if (_enumerator == null)
            {
                return result;
            }

            string defaultId = null;
            if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out IMMDevice defaultDevice) == 0 && defaultDevice != null)
            {
                defaultDevice.GetId(out defaultId);
                Marshal.ReleaseComObject(defaultDevice);
            }

            if (_enumerator.EnumAudioEndpoints(EDataFlow.Render, DEVICE_STATE_ACTIVE, out IMMDeviceCollection devices) != 0 || devices == null)
            {
                return result;
            }

            devices.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (devices.Item(i, out IMMDevice device) != 0 || device == null)
                {
                    continue;
                }

                device.GetId(out string id);
                result.Add(new OutputDevice
                {
                    Id = id,
                    Name = GetFriendlyName(device) ?? id,
                    IsDefault = id == defaultId
                });
                Marshal.ReleaseComObject(device);
            }

            Marshal.ReleaseComObject(devices);
            return result;
        }

        public void SetDefaultOutputDevice(string id)
        {
            try
            {
                var policy = (IPolicyConfig)new PolicyConfigClient();
                policy.SetDefaultEndpoint(id, ERole.Console);
                policy.SetDefaultEndpoint(id, ERole.Multimedia);
                policy.SetDefaultEndpoint(id, ERole.Communications);
                Marshal.ReleaseComObject(policy);
            }
            catch (Exception e)
            {
                ShellLogger.Error($"AudioController: Unable to change the default device: {e.Message}");
            }

            RefreshDefaultDevice();
        }

        private static string GetFriendlyName(IMMDevice device)
        {
            if (device.OpenPropertyStore(STGM_READ, out IPropertyStore store) != 0 || store == null)
            {
                return null;
            }

            try
            {
                PROPERTYKEY key = PKEY_Device_FriendlyName;
                if (store.GetValue(ref key, out PROPVARIANT value) != 0)
                {
                    return null;
                }

                string name = value.vt == VT_LPWSTR ? Marshal.PtrToStringUni(value.pointerValue) : null;
                PropVariantClear(ref value);
                return name;
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        private void ReleaseVolume()
        {
            if (_volume != null)
            {
                Marshal.ReleaseComObject(_volume);
                _volume = null;
            }
        }

        public void Dispose()
        {
            foreach (var app in _apps)
            {
                app.Release();
            }
            _apps.Clear();
            ReleaseVolume();
            if (_enumerator != null)
            {
                Marshal.ReleaseComObject(_enumerator);
                _enumerator = null;
            }
        }

        #region Interop
        private const int CLSCTX_ALL = 0x17;
        private const int DEVICE_STATE_ACTIVE = 0x1;
        private const int STGM_READ = 0;
        private const short VT_LPWSTR = 31;

        private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY
        {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            pid = 14
        };

        private enum EDataFlow { Render = 0, Capture = 1, All = 2 }
        private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public int pid;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public short vt;
            [FieldOffset(8)] public IntPtr pointerValue;
            [FieldOffset(16)] public IntPtr padding;
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class PolicyConfigClient { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
            [PreserveSig] int Commit();
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out int count);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
            [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
            [PreserveSig] int VolumeStepUp(ref Guid eventContext);
            [PreserveSig] int VolumeStepDown(ref Guid eventContext);
            [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
            [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
        }

        private const int AudioSessionStateExpired = 2;

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr sessionControl);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr audioVolume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
            [PreserveSig] int RegisterSessionNotification(IntPtr notification);
            [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
            [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
            [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
            [PreserveSig] int GetGroupingParam(out Guid groupingId);
            [PreserveSig] int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr notifications);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notifications);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out int processId);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        // Undocumented, but stable since Windows 7: the only way to change the default device.
        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat();
            [PreserveSig] int GetDeviceFormat();
            [PreserveSig] int ResetDeviceFormat();
            [PreserveSig] int SetDeviceFormat();
            [PreserveSig] int GetProcessingPeriod();
            [PreserveSig] int SetProcessingPeriod();
            [PreserveSig] int GetShareMode();
            [PreserveSig] int SetShareMode();
            [PreserveSig] int GetPropertyValue();
            [PreserveSig] int SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
            [PreserveSig] int SetEndpointVisibility();
        }
        #endregion
    }
}
