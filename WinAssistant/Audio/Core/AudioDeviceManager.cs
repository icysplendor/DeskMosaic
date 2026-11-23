using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WinAssistant.Audio.Diagnostics;
using WinAssistant.Audio.Models;

namespace WinAssistant.Audio.Core;

public sealed class AudioDeviceManager : IAudioDeviceManager
{
    private const int ClsCtxAll = 23;
    private const int AudioVolumeNotificationMutedOffset = 16;
    private const int AudioVolumeNotificationMasterVolumeOffset = 20;
    private static readonly Guid EnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid EnumeratorInterfaceId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid EndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid PolicyConfigClassId = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    private static readonly Guid PolicyConfigInterfaceId = new("f8679f50-850a-41cf-9c72-430f290290c8");

    private readonly IntPtr _enumerator;
    private readonly object _operationSync = new();
    private readonly object _notificationSync = new();
    private readonly Dictionary<AudioDeviceKind, EndpointVolumeSubscription> _volumeSubscriptions = [];
    private readonly ConcurrentDictionary<AudioDeviceKind, byte> _pendingVolumeRebinds = new();
    private readonly HashSet<AudioDeviceKind> _volumeBindingFailuresLogged = [];
    private AudioNotificationClient? _notificationClient;
    private bool _notificationsStarted;
    private bool _notificationRegistrationFailureLogged;
    private volatile bool _disposed;

    public event EventHandler<AudioDeviceKindEventArgs>? DefaultDeviceChanged;
    public event EventHandler<AudioVolumeChangedEventArgs>? VolumeChanged;

    public AudioDeviceManager()
    {
        _enumerator = CreateComInstance(EnumeratorClassId, EnumeratorInterfaceId);
    }

    public void Start()
    {
        lock (_operationSync)
        {
            StartCore();
        }
    }

    private void StartCore()
    {
        lock (_notificationSync)
        {
            if (_disposed)
            {
                return;
            }
            if (!_notificationsStarted)
            {
                _notificationClient = new AudioNotificationClient(this);
                try
                {
                    var register = GetMethod<RegisterEndpointNotificationCallbackDelegate>(_enumerator, 6);
                    HResult.ThrowIfFailed(register(_enumerator, _notificationClient));
                    _notificationsStarted = true;
                    if (_notificationRegistrationFailureLogged)
                    {
                        AppLogger.Info("Audio endpoint notification registration recovered.");
                        _notificationRegistrationFailureLogged = false;
                    }
                }
                catch (Exception exception)
                {
                    _notificationClient = null;
                    if (!_notificationRegistrationFailureLogged)
                    {
                        AppLogger.Error(exception, "Could not register audio endpoint notifications.");
                        _notificationRegistrationFailureLogged = true;
                    }
                }
            }

            var outputNeedsRebind = _pendingVolumeRebinds.TryRemove(AudioDeviceKind.Output, out _);
            if (!_volumeSubscriptions.ContainsKey(AudioDeviceKind.Output) || outputNeedsRebind)
            {
                TryBindVolumeNotification(AudioDeviceKind.Output);
            }
            var inputNeedsRebind = _pendingVolumeRebinds.TryRemove(AudioDeviceKind.Input, out _);
            if (!_volumeSubscriptions.ContainsKey(AudioDeviceKind.Input) || inputNeedsRebind)
            {
                TryBindVolumeNotification(AudioDeviceKind.Input);
            }
        }
    }

    public void Stop()
    {
        lock (_operationSync)
        {
            if (_disposed)
            {
                return;
            }
            StopCore();
        }
    }

    private void StopCore()
    {
        lock (_notificationSync)
        {
            foreach (var kind in _volumeSubscriptions.Keys.ToList())
            {
                UnbindVolumeNotification(kind);
            }

            if (_notificationsStarted && _notificationClient is not null)
            {
                var unregister = GetMethod<UnregisterEndpointNotificationCallbackDelegate>(_enumerator, 7);
                _ = unregister(_enumerator, _notificationClient);
                _notificationClient = null;
                _notificationsStarted = false;
            }
            _pendingVolumeRebinds.Clear();
        }
    }

    public IReadOnlyList<AudioDeviceInfo> Enumerate(AudioDeviceKind kind)
    {
        lock (_operationSync)
        {
            ThrowIfDisposed();
            return EnumerateCore(kind);
        }
    }

    public IReadOnlyList<AudioEndpointIdentity> EnumerateKnownEndpoints(AudioDeviceKind kind)
    {
        lock (_operationSync)
        {
            ThrowIfDisposed();
            var enumerate = GetMethod<EnumAudioEndpointsDelegate>(_enumerator, 3);
            HResult.ThrowIfFailed(enumerate(_enumerator, ToFlow(kind), DeviceState.All, out var collection));
            try
            {
                var getCount = GetMethod<DeviceCollectionGetCountDelegate>(collection, 3);
                HResult.ThrowIfFailed(getCount(collection, out var count));
                var item = GetMethod<DeviceCollectionItemDelegate>(collection, 4);
                var endpoints = new List<AudioEndpointIdentity>();
                for (uint index = 0; index < count; index++)
                {
                    HResult.ThrowIfFailed(item(collection, index, out var device));
                    try
                    {
                        endpoints.Add(new AudioEndpointIdentity(GetDeviceId(device), GetFriendlyName(device)));
                    }
                    catch (COMException)
                    {
                        // A disconnected endpoint may disappear while its properties are read.
                    }
                    finally
                    {
                        Marshal.Release(device);
                    }
                }
                return endpoints;
            }
            finally
            {
                Marshal.Release(collection);
            }
        }
    }

    private IReadOnlyList<AudioDeviceInfo> EnumerateCore(AudioDeviceKind kind)
    {
        var flow = ToFlow(kind);
        var defaultId = GetDefaultDeviceId(kind);
        var enumEndpoints = GetMethod<EnumAudioEndpointsDelegate>(_enumerator, 3);
        HResult.ThrowIfFailed(enumEndpoints(_enumerator, flow, DeviceState.Active, out var collection));

        try
        {
            var getCount = GetMethod<DeviceCollectionGetCountDelegate>(collection, 3);
            HResult.ThrowIfFailed(getCount(collection, out var count));

            var item = GetMethod<DeviceCollectionItemDelegate>(collection, 4);
            var devices = new List<AudioDeviceInfo>();
            for (uint i = 0; i < count; i++)
            {
                HResult.ThrowIfFailed(item(collection, i, out var device));
                try
                {
                    var id = GetDeviceId(device);
                    var name = GetFriendlyName(device);
                    var volumeState = GetVolumeState(device);
                    devices.Add(new AudioDeviceInfo(
                        id,
                        name,
                        kind,
                        AudioDeviceInfo.IdEquals(id, defaultId),
                        volumeState.Volume,
                        volumeState.IsMuted));
                }
                finally
                {
                    Marshal.Release(device);
                }
            }

            return devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally
        {
            Marshal.Release(collection);
        }
    }

    public string? GetDefaultDeviceId(AudioDeviceKind kind)
    {
        lock (_operationSync)
        {
            ThrowIfDisposed();
            return GetDefaultDeviceIdCore(kind);
        }
    }

    private string? GetDefaultDeviceIdCore(AudioDeviceKind kind)
    {
        var getDefault = GetMethod<GetDefaultAudioEndpointDelegate>(_enumerator, 4);
        var hr = getDefault(_enumerator, ToFlow(kind), ERole.Multimedia, out var device);
        if (hr < 0 || device == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return GetDeviceId(device);
        }
        finally
        {
            Marshal.Release(device);
        }
    }

    public void SetDefaultDevice(string deviceId)
    {
        lock (_operationSync)
        {
            ThrowIfDisposed();
            SetDefaultDeviceCore(deviceId);
        }
    }

    private static void SetDefaultDeviceCore(string deviceId)
    {
        var policyConfig = CreateComInstance(PolicyConfigClassId, PolicyConfigInterfaceId);
        try
        {
            var setDefaultEndpoint = GetMethod<SetDefaultEndpointDelegate>(policyConfig, 13);
            HResult.ThrowIfFailed(setDefaultEndpoint(policyConfig, deviceId, ERole.Console));
            HResult.ThrowIfFailed(setDefaultEndpoint(policyConfig, deviceId, ERole.Multimedia));
            HResult.ThrowIfFailed(setDefaultEndpoint(policyConfig, deviceId, ERole.Communications));
        }
        finally
        {
            Marshal.Release(policyConfig);
        }
    }

    public float GetVolume(string deviceId)
    {
        return GetVolumeState(deviceId).Volume;
    }

    public AudioDeviceVolumeState GetVolumeState(string deviceId)
    {
        lock (_operationSync)
        {
            ThrowIfDisposed();
            return GetVolumeStateCore(deviceId);
        }
    }

    private AudioDeviceVolumeState GetVolumeStateCore(string deviceId)
    {
        var device = GetDevice(deviceId);
        try
        {
            return GetVolumeState(device);
        }
        finally
        {
            Marshal.Release(device);
        }
    }

    public void SetVolume(string deviceId, float volume)
    {
        lock (_operationSync)
        {
            ThrowIfDisposed();
            SetVolumeCore(deviceId, volume);
        }
    }

    private void SetVolumeCore(string deviceId, float volume)
    {
        var device = GetDevice(deviceId);
        try
        {
            var endpoint = ActivateEndpointVolume(device);
            try
            {
                var setVolume = GetMethod<SetMasterVolumeLevelScalarDelegate>(endpoint, 7);
                var context = Guid.Empty;
                HResult.ThrowIfFailed(setVolume(endpoint, Math.Clamp(volume, 0f, 1f), ref context));
            }
            finally
            {
                Marshal.Release(endpoint);
            }
        }
        finally
        {
            Marshal.Release(device);
        }
    }

    private void HandleDefaultDeviceChanged(EDataFlow flow, ERole role)
    {
        if (role != ERole.Multimedia)
        {
            return;
        }

        var kind = ToKind(flow);
        if (kind is null)
        {
            return;
        }

        if (!_disposed)
        {
            // Never perform COM work while Windows is inside its notification
            // callback. SetDefaultEndpoint can wait for this callback to return;
            // rebinding here can deadlock it against discovery or volume polling.
            _pendingVolumeRebinds[kind.Value] = 0;
        }
        RaiseDefaultDeviceChanged(kind.Value);
    }

    private void HandleEndpointVolumeChanged(AudioDeviceKind kind, string deviceId, float volume, bool isMuted)
    {
        var eventArgs = new AudioVolumeChangedEventArgs(kind, deviceId, Math.Clamp(volume, 0f, 1f), isMuted);
        ThreadPool.QueueUserWorkItem(_ => VolumeChanged?.Invoke(this, eventArgs));
    }

    private void HandleDeviceListChanged(EDataFlow flow)
    {
        var kind = ToKind(flow);
        if (kind is not null)
        {
            RaiseDefaultDeviceChanged(kind.Value);
        }
    }

    private void RaiseDefaultDeviceChanged(AudioDeviceKind kind)
    {
        var eventArgs = new AudioDeviceKindEventArgs(kind);
        ThreadPool.QueueUserWorkItem(_ => DefaultDeviceChanged?.Invoke(this, eventArgs));
    }

    private void BindVolumeNotification(AudioDeviceKind kind)
    {
        UnbindVolumeNotification(kind);

        var deviceId = GetDefaultDeviceId(kind);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        var device = GetDevice(deviceId);
        var endpoint = IntPtr.Zero;
        try
        {
            endpoint = ActivateEndpointVolume(device);
            var callback = new EndpointVolumeCallback(this, kind, deviceId);
            var register = GetMethod<RegisterControlChangeNotifyDelegate>(endpoint, 3);
            HResult.ThrowIfFailed(register(endpoint, callback));
            _volumeSubscriptions[kind] = new EndpointVolumeSubscription(endpoint, callback);
            endpoint = IntPtr.Zero;
        }
        catch
        {
            if (endpoint != IntPtr.Zero)
            {
                Marshal.Release(endpoint);
            }

            if (_volumeSubscriptions.Remove(kind, out var subscription))
            {
                subscription.Dispose();
            }

            throw;
        }
        finally
        {
            Marshal.Release(device);
        }
    }

    private void TryBindVolumeNotification(AudioDeviceKind kind)
    {
        try
        {
            BindVolumeNotification(kind);
            if (_volumeSubscriptions.ContainsKey(kind)
                && _volumeBindingFailuresLogged.Remove(kind))
            {
                AppLogger.Info($"{kind} volume notification binding recovered.");
            }
        }
        catch (Exception exception)
        {
            if (_volumeBindingFailuresLogged.Add(kind))
            {
                AppLogger.Error(exception, $"Could not bind {kind} volume notifications.");
            }
        }
    }

    private void UnbindVolumeNotification(AudioDeviceKind kind)
    {
        if (_volumeSubscriptions.Remove(kind, out var subscription))
        {
            subscription.Dispose();
        }
    }

    private IntPtr GetDevice(string deviceId)
    {
        var getDevice = GetMethod<GetDeviceDelegate>(_enumerator, 5);
        HResult.ThrowIfFailed(getDevice(_enumerator, deviceId, out var device));
        return device;
    }

    private static string GetDeviceId(IntPtr device)
    {
        var getId = GetMethod<DeviceGetIdDelegate>(device, 5);
        HResult.ThrowIfFailed(getId(device, out var idPointer));
        try
        {
            return Marshal.PtrToStringUni(idPointer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(idPointer);
        }
    }

    private static string GetFriendlyName(IntPtr device)
    {
        var openPropertyStore = GetMethod<DeviceOpenPropertyStoreDelegate>(device, 4);
        HResult.ThrowIfFailed(openPropertyStore(device, StorageAccessMode.Read, out var propertyStore));
        try
        {
            var getValue = GetMethod<PropertyStoreGetValueDelegate>(propertyStore, 5);
            var key = PropertyKeys.FriendlyName;
            HResult.ThrowIfFailed(getValue(propertyStore, ref key, out var value));
            try
            {
                return value.GetString() ?? "Unknown audio device";
            }
            finally
            {
                value.Clear();
            }
        }
        finally
        {
            Marshal.Release(propertyStore);
        }
    }

    private static AudioDeviceVolumeState GetVolumeState(IntPtr device)
    {
        var endpoint = ActivateEndpointVolume(device);
        try
        {
            var getVolume = GetMethod<GetMasterVolumeLevelScalarDelegate>(endpoint, 9);
            var getMute = GetMethod<GetMuteDelegate>(endpoint, 15);
            HResult.ThrowIfFailed(getVolume(endpoint, out var volume));
            HResult.ThrowIfFailed(getMute(endpoint, out var isMuted));
            return new AudioDeviceVolumeState(Math.Clamp(volume, 0f, 1f), isMuted);
        }
        finally
        {
            Marshal.Release(endpoint);
        }
    }

    private static IntPtr ActivateEndpointVolume(IntPtr device)
    {
        var activate = GetMethod<DeviceActivateDelegate>(device, 3);
        var iid = EndpointVolumeId;
        HResult.ThrowIfFailed(activate(device, ref iid, ClsCtxAll, IntPtr.Zero, out var endpoint));
        return endpoint;
    }

    private static IntPtr CreateComInstance(Guid classId, Guid interfaceId)
    {
        HResult.ThrowIfFailed(CoCreateInstance(
            ref classId,
            IntPtr.Zero,
            ClsCtxAll,
            ref interfaceId,
            out var instance));
        return instance;
    }

    private static T GetMethod<T>(IntPtr instance, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(instance);
        var method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    private static EDataFlow ToFlow(AudioDeviceKind kind)
    {
        return kind == AudioDeviceKind.Input ? EDataFlow.Capture : EDataFlow.Render;
    }

    private static AudioDeviceKind? ToKind(EDataFlow flow)
    {
        return flow switch
        {
            EDataFlow.Render => AudioDeviceKind.Output,
            EDataFlow.Capture => AudioDeviceKind.Input,
            _ => null
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        lock (_operationSync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            StopCore();

            if (_enumerator != IntPtr.Zero)
            {
                Marshal.Release(_enumerator);
            }
        }
    }

    private sealed class AudioNotificationClient(AudioDeviceManager owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            owner.HandleDeviceListChanged(EDataFlow.Render);
            owner.HandleDeviceListChanged(EDataFlow.Capture);
            return 0;
        }

        public int OnDeviceAdded(string deviceId)
        {
            owner.HandleDeviceListChanged(EDataFlow.Render);
            owner.HandleDeviceListChanged(EDataFlow.Capture);
            return 0;
        }

        public int OnDeviceRemoved(string deviceId)
        {
            owner.HandleDeviceListChanged(EDataFlow.Render);
            owner.HandleDeviceListChanged(EDataFlow.Capture);
            return 0;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            owner.HandleDefaultDeviceChanged(flow, role);
            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            owner.HandleDeviceListChanged(EDataFlow.Render);
            owner.HandleDeviceListChanged(EDataFlow.Capture);
            return 0;
        }
    }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class EndpointVolumeCallback(AudioDeviceManager owner, AudioDeviceKind kind, string deviceId) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr notifyData)
        {
            if (notifyData != IntPtr.Zero)
            {
                var volume = Marshal.PtrToStructure<float>(
                    IntPtr.Add(notifyData, AudioVolumeNotificationMasterVolumeOffset));
                var isMuted = Marshal.ReadInt32(notifyData, AudioVolumeNotificationMutedOffset) != 0;
                owner.HandleEndpointVolumeChanged(kind, deviceId, volume, isMuted);
            }

            return 0;
        }
    }

    private sealed class EndpointVolumeSubscription(IntPtr endpoint, IAudioEndpointVolumeCallback callback) : IDisposable
    {
        public void Dispose()
        {
            var unregister = GetMethod<UnregisterControlChangeNotifyDelegate>(endpoint, 4);
            _ = unregister(endpoint, callback);
            Marshal.Release(endpoint);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr outer,
        int clsContext,
        ref Guid iid,
        out IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAudioEndpointsDelegate(IntPtr self, EDataFlow dataFlow, DeviceState stateMask, out IntPtr devices);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDefaultAudioEndpointDelegate(IntPtr self, EDataFlow dataFlow, ERole role, out IntPtr endpoint);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDeviceDelegate(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RegisterEndpointNotificationCallbackDelegate(
        IntPtr self,
        [MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnregisterEndpointNotificationCallbackDelegate(
        IntPtr self,
        [MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeviceCollectionGetCountDelegate(IntPtr self, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeviceCollectionItemDelegate(IntPtr self, uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeviceActivateDelegate(IntPtr self, ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr interfacePointer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeviceOpenPropertyStoreDelegate(IntPtr self, StorageAccessMode accessMode, out IntPtr properties);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeviceGetIdDelegate(IntPtr self, out IntPtr id);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyStoreGetValueDelegate(IntPtr self, ref PropertyKey key, out PropVariant value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetMasterVolumeLevelScalarDelegate(IntPtr self, float level, ref Guid eventContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMasterVolumeLevelScalarDelegate(IntPtr self, out float level);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMuteDelegate(IntPtr self, [MarshalAs(UnmanagedType.Bool)] out bool isMuted);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RegisterControlChangeNotifyDelegate(
        IntPtr self,
        [MarshalAs(UnmanagedType.Interface)] IAudioEndpointVolumeCallback notify);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnregisterControlChangeNotifyDelegate(
        IntPtr self,
        [MarshalAs(UnmanagedType.Interface)] IAudioEndpointVolumeCallback notify);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetDefaultEndpointDelegate(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
}
