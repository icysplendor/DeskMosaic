using WinAssistant.Audio.Diagnostics;
using WinAssistant.Audio.Core;
using WinAssistant.Audio.Models;
using WinAssistant.Audio.Storage;
using WinAssistant.Audio.UI;

namespace WinAssistant.Audio.Services;

public sealed class AppStateService : IDisposable
{
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StartupTopologyPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SteadyTopologyPollInterval = TimeSpan.FromSeconds(15);
    private const int StartupTopologyPollCount = 45;

    private readonly IAudioDeviceManager _audio;
    private readonly IConfigStore _store;
    private readonly AppConfig _config;
    private readonly object _syncRoot = new();
    private readonly System.Threading.Timer _volumeTimer;
    private readonly System.Threading.Timer _deviceRefreshTimer;
    private readonly System.Threading.Timer _refreshDebounceTimer;
    private readonly System.Threading.Timer _saveDebounceTimer;
    private readonly HashSet<AudioDeviceKind> _userSwitches = [];
    private readonly HashSet<AudioDeviceKind> _enumerationFailuresLogged = [];
    private readonly HashSet<AudioDeviceKind> _knownEndpointFailuresLogged = [];
    private int _startupTopologyPollsRemaining;
    private bool _topologyRefreshFailureLogged;
    private int _profileSwitchInProgress;
    private int _profileSwitchGeneration;
    private bool _disposed;
    private AppStateSnapshot _snapshot;

    public event EventHandler<AppStateSnapshot>? StateChanged;

    public AppStateService(IAudioDeviceManager audio, IConfigStore store, AppConfig config)
    {
        _audio = audio;
        _store = store;
        _config = config;
        _snapshot = BuildDisabledSnapshot() with { AudioEnabled = config.AudioEnabled };
        _volumeTimer = new System.Threading.Timer(_ => RefreshAndRememberVolumes(), null, Timeout.Infinite, Timeout.Infinite);
        _deviceRefreshTimer = new System.Threading.Timer(_ => RefreshDeviceTopology(), null, Timeout.Infinite, Timeout.Infinite);
        _refreshDebounceTimer = new System.Threading.Timer(_ => RefreshFromAudioNotification(), null, Timeout.Infinite, Timeout.Infinite);
        _saveDebounceTimer = new System.Threading.Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        _audio.DefaultDeviceChanged += OnDefaultDeviceChanged;
        _audio.VolumeChanged += OnVolumeChanged;
    }

    public AppStateSnapshot Snapshot
    {
        get
        {
            lock (_syncRoot)
            {
                return _snapshot;
            }
        }
    }

    public HotkeyConfig Hotkeys => _config.Hotkeys;

    public void SetTheme(string theme)
    {
        _config.Theme = ThemeManager.NormalizeTheme(theme);
        ThemeManager.Apply(_config.Theme);
        SaveSoon();
        Refresh();
    }

    public void SetLanguage(string language)
    {
        _config.Language = LocalizationManager.NormalizeLanguage(language);
        SaveSoon();
        Refresh();
    }

    public void SetHotkeys(string nextProfile, string previousProfile)
    {
        _config.Hotkeys.NextProfile = nextProfile.Trim();
        _config.Hotkeys.PreviousProfile = previousProfile.Trim();
        SaveSoon();
        Refresh();
    }

    public void SetStartupOptions(bool startWithWindows, bool silentStart)
    {
        _config.StartWithWindows = startWithWindows;
        _config.SilentStart = silentStart;
        SaveSoon();
        Refresh();
    }

    public void Start()
    {
        if (!_config.AudioEnabled)
        {
            Refresh();
            return;
        }
        StartAudioRuntime();
    }

    public void SetAudioEnabled(bool enabled)
    {
        lock (_syncRoot)
        {
            if (_config.AudioEnabled == enabled)
            {
                return;
            }
            _config.AudioEnabled = enabled;
        }

        if (enabled)
        {
            StartAudioRuntime();
        }
        else
        {
            Interlocked.Increment(ref _profileSwitchGeneration);
            lock (_syncRoot)
            {
                StopAudioRuntime();
                _userSwitches.Clear();
                _snapshot = BuildDisabledSnapshot();
            }
            StateChanged?.Invoke(this, Snapshot);
            SaveSoon();
        }
    }

    public void SetProfileDeviceRecovery(bool enabled)
    {
        if (_config.RecoverProfileDevicesByName == enabled) return;
        _config.RecoverProfileDevicesByName = enabled;
        SaveSoon();
        Refresh();
    }

    private void StartAudioRuntime()
    {
        _audio.Start();
        Refresh();
        SaveSoon();
        _volumeTimer.Change(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        Interlocked.Exchange(ref _startupTopologyPollsRemaining, StartupTopologyPollCount);
        _deviceRefreshTimer.Change(StartupTopologyPollInterval, Timeout.InfiniteTimeSpan);
    }

    public void SetLock(AudioDeviceKind kind, bool enabled)
    {
        if (!Snapshot.AudioEnabled) return;
        var current = kind == AudioDeviceKind.Output ? Snapshot.CurrentOutput : Snapshot.CurrentInput;
        if (kind == AudioDeviceKind.Output)
        {
            _config.LockOutputDevice = enabled;
            if (enabled) _config.PreferredOutputDeviceId = current?.Id;
        }
        else
        {
            _config.LockInputDevice = enabled;
            if (enabled) _config.PreferredInputDeviceId = current?.Id;
        }

        SaveSoon();
        Refresh();
    }

    public void SetVolume(AudioDeviceInfo device, float volume)
    {
        if (!Snapshot.AudioEnabled) return;
        var normalized = Math.Clamp(volume, 0f, 1f);
        _audio.SetVolume(device.Id, normalized);
        GetVolumes(device.Kind)[device.Id] = normalized;
        SaveSoon();
        Refresh();
    }

    public void AddCurrentProfile()
    {
        var snapshot = Snapshot;
        if (!snapshot.AudioEnabled) return;
        if (snapshot.CurrentOutput is null || snapshot.CurrentInput is null)
        {
            return;
        }
        var profile = new AudioConfigurationProfile
        {
            Name = $"配置 {_config.Profiles.Count + 1}",
            OutputDeviceId = snapshot.CurrentOutput.Id,
            InputDeviceId = snapshot.CurrentInput.Id,
            OutputDeviceName = snapshot.CurrentOutput.Name,
            InputDeviceName = snapshot.CurrentInput.Name
        };
        _config.Profiles.Add(profile);
        _config.ActiveProfileId = profile.Id;
        _config.PreferredOutputDeviceId = profile.OutputDeviceId;
        _config.PreferredInputDeviceId = profile.InputDeviceId;
        SaveSoon();
        Refresh();
    }

    public void UpdateProfile(string id, string name, string outputDeviceId, string inputDeviceId)
    {
        var profile = _config.Profiles.FirstOrDefault(item => item.Id == id);
        if (profile is null || string.IsNullOrWhiteSpace(outputDeviceId) || string.IsNullOrWhiteSpace(inputDeviceId))
        {
            return;
        }
        var previousOutputId = profile.OutputDeviceId;
        var previousInputId = profile.InputDeviceId;
        profile.Name = string.IsNullOrWhiteSpace(name) ? $"配置 {_config.Profiles.IndexOf(profile) + 1}" : name.Trim();
        profile.OutputDeviceId = outputDeviceId;
        profile.InputDeviceId = inputDeviceId;
        profile.OutputDeviceName = Snapshot.AllOutputs.FirstOrDefault(device => AudioDeviceInfo.IdEquals(device.Id, outputDeviceId))?.Name
            ?? (AudioDeviceInfo.IdEquals(outputDeviceId, previousOutputId) ? profile.OutputDeviceName : null);
        profile.InputDeviceName = Snapshot.AllInputs.FirstOrDefault(device => AudioDeviceInfo.IdEquals(device.Id, inputDeviceId))?.Name
            ?? (AudioDeviceInfo.IdEquals(inputDeviceId, previousInputId) ? profile.InputDeviceName : null);
        if (_config.ActiveProfileId == id)
        {
            _config.PreferredOutputDeviceId = outputDeviceId;
            _config.PreferredInputDeviceId = inputDeviceId;
        }
        SaveSoon();
        Refresh();
    }

    public void DeleteProfile(string id)
    {
        _config.Profiles.RemoveAll(profile => profile.Id == id);
        if (_config.ActiveProfileId == id)
        {
            _config.ActiveProfileId = null;
        }
        SaveSoon();
        Refresh();
    }

    public void ActivateProfile(string id)
    {
        if (!Snapshot.AudioEnabled) return;
        var profile = _config.Profiles.FirstOrDefault(item => item.Id == id);
        if (profile is null)
        {
            return;
        }
        var snapshot = Snapshot;
        var output = snapshot.AllOutputs.FirstOrDefault(device => AudioDeviceInfo.IdEquals(device.Id, profile.OutputDeviceId));
        var input = snapshot.AllInputs.FirstOrDefault(device => AudioDeviceInfo.IdEquals(device.Id, profile.InputDeviceId));
        if (output is null || input is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _profileSwitchInProgress, 1, 0) != 0)
        {
            return;
        }

        var generation = Volatile.Read(ref _profileSwitchGeneration);
        _ = Task.Run(() => ActivateProfileCore(profile, snapshot, output, input, generation));
    }

    private void ActivateProfileCore(
        AudioConfigurationProfile profile,
        AppStateSnapshot snapshot,
        AudioDeviceInfo output,
        AudioDeviceInfo input,
        int generation)
    {
        try
        {
            if (!IsProfileSwitchCurrent(generation)) return;
            RememberCurrentVolume(AudioDeviceKind.Output);
            RememberCurrentVolume(AudioDeviceKind.Input);
            MarkUserSwitch(AudioDeviceKind.Output, output.Id);
            MarkUserSwitch(AudioDeviceKind.Input, input.Id);
            _config.ActiveProfileId = profile.Id;
            if (snapshot.CurrentOutput?.Id != output.Id)
            {
                _audio.SetDefaultDevice(output.Id);
            }
            if (snapshot.CurrentInput?.Id != input.Id)
            {
                if (snapshot.CurrentOutput?.Id != output.Id)
                {
                    Thread.Sleep(75);
                }
                if (!IsProfileSwitchCurrent(generation)) return;
                _audio.SetDefaultDevice(input.Id);
            }
            if (!IsProfileSwitchCurrent(generation)) return;
            if (_config.OutputVolumes.TryGetValue(output.Id, out var outputVolume)) _audio.SetVolume(output.Id, outputVolume);
            if (_config.InputVolumes.TryGetValue(input.Id, out var inputVolume)) _audio.SetVolume(input.Id, inputVolume);
            SaveSoon();
            Refresh();
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                AppLogger.Error(exception, $"Could not activate audio profile {profile.Id}.");
                Refresh();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _profileSwitchInProgress, 0);
        }
    }

    private bool IsProfileSwitchCurrent(int generation)
    {
        return !_disposed
            && _config.AudioEnabled
            && Volatile.Read(ref _profileSwitchGeneration) == generation;
    }

    public void CycleProfile(int direction)
    {
        var snapshot = Snapshot;
        if (!snapshot.AudioEnabled) return;
        var available = _config.Profiles.Where(profile =>
            snapshot.AllOutputs.Any(device => AudioDeviceInfo.IdEquals(device.Id, profile.OutputDeviceId))
            && snapshot.AllInputs.Any(device => AudioDeviceInfo.IdEquals(device.Id, profile.InputDeviceId))).ToList();
        if (available.Count == 0)
        {
            return;
        }
        var currentIndex = available.FindIndex(profile =>
            AudioDeviceInfo.IdEquals(profile.OutputDeviceId, snapshot.CurrentOutput?.Id)
            && AudioDeviceInfo.IdEquals(profile.InputDeviceId, snapshot.CurrentInput?.Id));
        if (currentIndex < 0)
        {
            currentIndex = available.FindIndex(profile => profile.Id == _config.ActiveProfileId);
        }
        var offset = direction < 0 ? -1 : 1;
        var nextIndex = currentIndex < 0
            ? (offset < 0 ? available.Count - 1 : 0)
            : (currentIndex + offset + available.Count) % available.Count;
        ActivateProfile(available[nextIndex].Id);
    }

    public void Refresh()
    {
        lock (_syncRoot)
        {
            _snapshot = _config.AudioEnabled ? BuildSnapshot() : BuildDisabledSnapshot();
        }

        StateChanged?.Invoke(this, Snapshot);
    }

    private AppStateSnapshot BuildSnapshot()
    {
        var outputs = SafeEnumerate(AudioDeviceKind.Output);
        var inputs = SafeEnumerate(AudioDeviceKind.Input);
        return BuildSnapshot(outputs, inputs);
    }

    private AppStateSnapshot BuildSnapshot(
        IReadOnlyList<AudioDeviceInfo> outputs,
        IReadOnlyList<AudioDeviceInfo> inputs)
    {
        var currentOutput = outputs.FirstOrDefault(d => d.IsDefault);
        var currentInput = inputs.FirstOrDefault(d => d.IsDefault);
        ReconcileProfileDevices(outputs, inputs);
        EnsureActiveProfile(currentOutput, currentInput);
        RestoreLockedDeviceIfNeeded(AudioDeviceKind.Output, currentOutput, outputs);
        RestoreLockedDeviceIfNeeded(AudioDeviceKind.Input, currentInput, inputs);

        if (currentOutput is not null && string.IsNullOrWhiteSpace(_config.PreferredOutputDeviceId))
        {
            _config.PreferredOutputDeviceId = currentOutput.Id;
        }

        if (currentInput is not null && string.IsNullOrWhiteSpace(_config.PreferredInputDeviceId))
        {
            _config.PreferredInputDeviceId = currentInput.Id;
        }

        return new AppStateSnapshot(
            outputs,
            inputs,
            currentOutput,
            currentInput,
            true,
            _config.RecoverProfileDevicesByName,
            _config.LockOutputDevice,
            _config.LockInputDevice,
            _config.StartWithWindows,
            _config.SilentStart,
            ThemeManager.NormalizeTheme(_config.Theme),
            LocalizationManager.NormalizeLanguage(_config.Language),
            _config.Profiles.ToList(),
            _config.ActiveProfileId,
            _config.Hotkeys.NextProfile,
            _config.Hotkeys.PreviousProfile);
    }

    private AppStateSnapshot BuildDisabledSnapshot()
    {
        return new AppStateSnapshot(
            [], [], null, null, false, _config.RecoverProfileDevicesByName,
            _config.LockOutputDevice,
            _config.LockInputDevice,
            _config.StartWithWindows,
            _config.SilentStart,
            ThemeManager.NormalizeTheme(_config.Theme),
            LocalizationManager.NormalizeLanguage(_config.Language),
            _config.Profiles.ToList(),
            _config.ActiveProfileId,
            _config.Hotkeys.NextProfile,
            _config.Hotkeys.PreviousProfile);
    }

    private void StopAudioRuntime()
    {
        _volumeTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _deviceRefreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _refreshDebounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        Interlocked.Exchange(ref _startupTopologyPollsRemaining, 0);
        _audio.Stop();
    }

    private void EnsureActiveProfile(AudioDeviceInfo? currentOutput, AudioDeviceInfo? currentInput)
    {
        if (_config.ActiveProfileId is null || (!_config.LockOutputDevice && !_config.LockInputDevice))
        {
            _config.ActiveProfileId = _config.Profiles.FirstOrDefault(profile =>
                AudioDeviceInfo.IdEquals(profile.OutputDeviceId, currentOutput?.Id)
                && AudioDeviceInfo.IdEquals(profile.InputDeviceId, currentInput?.Id))?.Id;
        }
    }

    private bool ReconcileProfileDevices(
        IReadOnlyList<AudioDeviceInfo> outputs,
        IReadOnlyList<AudioDeviceInfo> inputs)
    {
        if (_config.Profiles.Count == 0) return false;
        IReadOnlyList<AudioEndpointIdentity> knownOutputs = _config.RecoverProfileDevicesByName
            ? SafeEnumerateKnownEndpoints(AudioDeviceKind.Output, outputs)
            : [];
        IReadOnlyList<AudioEndpointIdentity> knownInputs = _config.RecoverProfileDevicesByName
            ? SafeEnumerateKnownEndpoints(AudioDeviceKind.Input, inputs)
            : [];
        var changed = false;
        foreach (var profile in _config.Profiles)
        {
            changed |= ReconcileProfileDevice(profile, AudioDeviceKind.Output, outputs, knownOutputs);
            changed |= ReconcileProfileDevice(profile, AudioDeviceKind.Input, inputs, knownInputs);
        }
        if (changed) SaveSoon();
        return changed;
    }

    private IReadOnlyList<AudioEndpointIdentity> SafeEnumerateKnownEndpoints(
        AudioDeviceKind kind,
        IReadOnlyList<AudioDeviceInfo> active)
    {
        try
        {
            var known = _audio.EnumerateKnownEndpoints(kind);
            if (_knownEndpointFailuresLogged.Remove(kind))
                AppLogger.Info($"{kind} known audio endpoint enumeration recovered.");
            return known;
        }
        catch (Exception exception)
        {
            if (_knownEndpointFailuresLogged.Add(kind))
                AppLogger.Error(exception, $"Could not enumerate known {kind} audio endpoints.");
            return active.Select(device => new AudioEndpointIdentity(device.Id, device.Name)).ToList();
        }
    }

    private bool ReconcileProfileDevice(
        AudioConfigurationProfile profile,
        AudioDeviceKind kind,
        IReadOnlyList<AudioDeviceInfo> devices,
        IReadOnlyList<AudioEndpointIdentity> knownEndpoints)
    {
        var isOutput = kind == AudioDeviceKind.Output;
        var savedId = isOutput ? profile.OutputDeviceId : profile.InputDeviceId;
        var savedName = isOutput ? profile.OutputDeviceName : profile.InputDeviceName;
        var byId = devices.FirstOrDefault(device => AudioDeviceInfo.IdEquals(device.Id, savedId));
        if (byId is not null)
        {
            if (string.Equals(savedName, byId.Name, StringComparison.Ordinal)) return false;
            if (isOutput) profile.OutputDeviceName = byId.Name;
            else profile.InputDeviceName = byId.Name;
            return true;
        }

        if (!_config.RecoverProfileDevicesByName || string.IsNullOrWhiteSpace(savedName)) return false;
        var activeMatches = devices.Where(device => string.Equals(device.Name, savedName, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        if (activeMatches.Count > 1) return false;
        AudioEndpointIdentity? replacement = activeMatches.Count == 1
            ? new AudioEndpointIdentity(activeMatches[0].Id, activeMatches[0].Name)
            : null;
        if (replacement is null)
        {
            // An endpoint that Windows still knows by its saved ID may simply be unplugged.
            // Keep that binding instead of alternating between two offline IDs.
            if (knownEndpoints.Any(endpoint => AudioDeviceInfo.IdEquals(endpoint.Id, savedId))) return false;
            var offlineMatches = knownEndpoints.Where(endpoint =>
                !AudioDeviceInfo.IdEquals(endpoint.Id, savedId)
                && !devices.Any(device => AudioDeviceInfo.IdEquals(device.Id, endpoint.Id))
                && string.Equals(endpoint.Name, savedName, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (offlineMatches.Count != 1) return false;
            replacement = offlineMatches[0];
        }
        if (isOutput)
        {
            profile.OutputDeviceId = replacement.Id;
            profile.OutputDeviceName = replacement.Name;
            if (AudioDeviceInfo.IdEquals(_config.PreferredOutputDeviceId, savedId))
                _config.PreferredOutputDeviceId = replacement.Id;
            if (_config.OutputVolumes.TryGetValue(savedId, out var volume)
                && !_config.OutputVolumes.ContainsKey(replacement.Id))
                _config.OutputVolumes[replacement.Id] = volume;
        }
        else
        {
            profile.InputDeviceId = replacement.Id;
            profile.InputDeviceName = replacement.Name;
            if (AudioDeviceInfo.IdEquals(_config.PreferredInputDeviceId, savedId))
                _config.PreferredInputDeviceId = replacement.Id;
            if (_config.InputVolumes.TryGetValue(savedId, out var volume)
                && !_config.InputVolumes.ContainsKey(replacement.Id))
                _config.InputVolumes[replacement.Id] = volume;
        }
        AppLogger.Info($"Rebound {kind} in audio profile {profile.Id} to endpoint {replacement.Id} by unique device name.");
        return true;
    }

    private void RestoreLockedDeviceIfNeeded(
        AudioDeviceKind kind,
        AudioDeviceInfo? current,
        IReadOnlyList<AudioDeviceInfo> availableDevices)
    {
        var preferred = kind == AudioDeviceKind.Output
            ? _config.PreferredOutputDeviceId
            : _config.PreferredInputDeviceId;
        var locked = kind == AudioDeviceKind.Output ? _config.LockOutputDevice : _config.LockInputDevice;

        if (!locked
            || string.IsNullOrWhiteSpace(preferred)
            || !availableDevices.Any(device => AudioDeviceInfo.IdEquals(device.Id, preferred)))
        {
            return;
        }

        if (AudioDeviceInfo.IdEquals(current?.Id, preferred))
        {
            _userSwitches.Remove(kind);
            return;
        }

        if (_userSwitches.Remove(kind))
        {
            return;
        }

        try
        {
            _audio.SetDefaultDevice(preferred);
        }
        catch (Exception exception)
        {
            AppLogger.Error(exception, $"Could not restore locked {kind} audio device.");
        }
    }

    private IReadOnlyList<AudioDeviceInfo> SafeEnumerate(AudioDeviceKind kind)
    {
        return TryEnumerate(kind, out var devices) ? devices : [];
    }

    private bool TryEnumerate(AudioDeviceKind kind, out IReadOnlyList<AudioDeviceInfo> devices)
    {
        try
        {
            devices = _audio.Enumerate(kind);
            if (_enumerationFailuresLogged.Remove(kind))
            {
                AppLogger.Info($"{kind} audio device enumeration recovered.");
            }

            return true;
        }
        catch (Exception exception)
        {
            if (_enumerationFailuresLogged.Add(kind))
            {
                AppLogger.Error(exception, $"Could not enumerate {kind} audio devices.");
            }

            devices = [];
            return false;
        }
    }

    private void MarkUserSwitch(AudioDeviceKind kind, string deviceId)
    {
        lock (_syncRoot)
        {
            _userSwitches.Add(kind);
            SetPreferred(kind, deviceId);
        }
    }

    private void SetPreferred(AudioDeviceKind kind, string deviceId)
    {
        if (kind == AudioDeviceKind.Output)
        {
            _config.PreferredOutputDeviceId = deviceId;
        }
        else
        {
            _config.PreferredInputDeviceId = deviceId;
        }
    }

    private void RememberCurrentVolume(AudioDeviceKind kind)
    {
        var current = kind == AudioDeviceKind.Output ? Snapshot.CurrentOutput : Snapshot.CurrentInput;
        if (current is null)
        {
            return;
        }

        GetVolumes(kind)[current.Id] = _audio.GetVolume(current.Id);
    }

    private void RefreshAndRememberVolumes()
    {
        if (_disposed || !_config.AudioEnabled)
        {
            return;
        }

        try
        {
            var snapshot = Snapshot;
            if (snapshot.CurrentOutput is not null)
            {
                RefreshCurrentVolumeState(snapshot.CurrentOutput);
            }

            if (snapshot.CurrentInput is not null)
            {
                RefreshCurrentVolumeState(snapshot.CurrentInput);
            }
        }
        catch
        {
            // Device state can change while Windows is re-enumerating endpoints.
        }
    }

    private void RefreshDeviceTopology()
    {
        if (_disposed || !_config.AudioEnabled)
        {
            return;
        }

        try
        {
            // Login startup can precede Windows Audio, USB and Bluetooth endpoint
            // readiness. Re-running Start also recovers notification registration
            // or default-volume subscriptions that were unavailable at that time.
            if (_disposed || !_config.AudioEnabled)
            {
                return;
            }
            _audio.Start();
            if (!TryEnumerate(AudioDeviceKind.Output, out var outputs)
                || !TryEnumerate(AudioDeviceKind.Input, out var inputs))
            {
                return;
            }
            if (_topologyRefreshFailureLogged)
            {
                AppLogger.Info("Audio device topology refresh recovered.");
                _topologyRefreshFailureLogged = false;
            }

            AppStateSnapshot next;
            lock (_syncRoot)
            {
                var previous = _snapshot;
                if (DeviceTopologyMatches(previous.AllOutputs, outputs)
                    && DeviceTopologyMatches(previous.AllInputs, inputs)
                    && !ReconcileProfileDevices(outputs, inputs))
                {
                    return;
                }
                next = BuildSnapshot(outputs, inputs);
                _snapshot = next;
            }
            StateChanged?.Invoke(this, next);
        }
        catch (Exception exception)
        {
            if (!_topologyRefreshFailureLogged)
            {
                AppLogger.Error(exception, "Could not refresh audio device topology.");
                _topologyRefreshFailureLogged = true;
            }
        }
        finally
        {
            ScheduleNextDeviceTopologyRefresh();
        }
    }

    private void ScheduleNextDeviceTopologyRefresh()
    {
        if (_disposed || !_config.AudioEnabled)
        {
            return;
        }

        var remaining = Volatile.Read(ref _startupTopologyPollsRemaining);
        if (remaining > 0)
        {
            remaining = Interlocked.Decrement(ref _startupTopologyPollsRemaining);
        }
        var delay = remaining > 0 ? StartupTopologyPollInterval : SteadyTopologyPollInterval;
        _deviceRefreshTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private static bool DeviceTopologyMatches(
        IReadOnlyList<AudioDeviceInfo> current,
        IReadOnlyList<AudioDeviceInfo> incoming)
    {
        if (current.Count != incoming.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            var left = current[index];
            var right = incoming[index];
            if (left.Id != right.Id
                || left.Name != right.Name
                || left.Kind != right.Kind
                || left.IsDefault != right.IsDefault)
            {
                return false;
            }
        }

        return true;
    }

    private void OnDefaultDeviceChanged(object? sender, AudioDeviceKindEventArgs e)
    {
        ScheduleRefresh(TimeSpan.FromMilliseconds(150));
    }

    private void OnVolumeChanged(object? sender, AudioVolumeChangedEventArgs e)
    {
        var changed = ApplyVolumeStateChange(e.Kind, e.DeviceId, new AudioDeviceVolumeState(e.Volume, e.IsMuted));
        var volumes = GetVolumes(e.Kind);
        if (changed && (!volumes.TryGetValue(e.DeviceId, out var rememberedVolume)
                        || Math.Abs(rememberedVolume - e.Volume) > 0.0005f))
        {
            volumes[e.DeviceId] = e.Volume;
            SaveSoon();
        }
    }

    private void RefreshCurrentVolumeState(AudioDeviceInfo device)
    {
        var volumeState = _audio.GetVolumeState(device.Id);
        if (!HasVolumeStateChanged(device, volumeState))
        {
            return;
        }

        if (Math.Abs(device.Volume - volumeState.Volume) > 0.0005f)
        {
            GetVolumes(device.Kind)[device.Id] = volumeState.Volume;
            SaveSoon();
        }

        _ = ApplyVolumeStateChange(device.Kind, device.Id, volumeState);
    }

    private bool ApplyVolumeStateChange(AudioDeviceKind kind, string deviceId, AudioDeviceVolumeState volumeState)
    {
        AppStateSnapshot nextSnapshot;
        var changed = false;

        lock (_syncRoot)
        {
            var snapshot = _snapshot;
            var allOutputs = snapshot.AllOutputs;
            var allInputs = snapshot.AllInputs;
            var currentOutput = snapshot.CurrentOutput;
            var currentInput = snapshot.CurrentInput;

            if (kind == AudioDeviceKind.Output)
            {
                allOutputs = UpdateVolumeState(allOutputs, deviceId, volumeState, ref changed);
                currentOutput = UpdateVolumeState(currentOutput, deviceId, volumeState, ref changed);
            }
            else
            {
                allInputs = UpdateVolumeState(allInputs, deviceId, volumeState, ref changed);
                currentInput = UpdateVolumeState(currentInput, deviceId, volumeState, ref changed);
            }

            if (!changed)
            {
                return false;
            }

            nextSnapshot = snapshot with
            {
                AllOutputs = allOutputs,
                AllInputs = allInputs,
                CurrentOutput = currentOutput,
                CurrentInput = currentInput
            };
            _snapshot = nextSnapshot;
        }

        StateChanged?.Invoke(this, nextSnapshot);
        return true;
    }

    private static IReadOnlyList<AudioDeviceInfo> UpdateVolumeState(
        IReadOnlyList<AudioDeviceInfo> devices,
        string deviceId,
        AudioDeviceVolumeState volumeState,
        ref bool changed)
    {
        List<AudioDeviceInfo>? updated = null;
        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            var nextDevice = UpdateVolumeState(device, deviceId, volumeState, ref changed);
            if (nextDevice is null)
            {
                continue;
            }

            updated ??= devices.ToList();
            updated[index] = nextDevice;
        }

        return updated ?? devices;
    }

    private static AudioDeviceInfo? UpdateVolumeState(
        AudioDeviceInfo? device,
        string deviceId,
        AudioDeviceVolumeState volumeState,
        ref bool changed)
    {
        if (device is null || device.Id != deviceId || !HasVolumeStateChanged(device, volumeState))
        {
            return null;
        }

        changed = true;
        return device with
        {
            Volume = volumeState.Volume,
            IsMuted = volumeState.IsMuted
        };
    }

    private static bool HasVolumeStateChanged(AudioDeviceInfo device, AudioDeviceVolumeState volumeState)
    {
        return Math.Abs(device.Volume - volumeState.Volume) > 0.0005f || device.IsMuted != volumeState.IsMuted;
    }

    private void ScheduleRefresh(TimeSpan delay)
    {
        if (!_disposed && _config.AudioEnabled)
        {
            _refreshDebounceTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void RefreshFromAudioNotification()
    {
        if (_disposed || !_config.AudioEnabled)
        {
            return;
        }

        try
        {
            // Rebind only after the Windows default-device callback has returned.
            // Performing COM work inside that callback can deadlock a switch.
            _audio.Start();
            Refresh();
        }
        catch
        {
            // Audio notifications can arrive while Windows is switching endpoints.
        }
    }

    private Dictionary<string, float> GetVolumes(AudioDeviceKind kind)
    {
        return kind == AudioDeviceKind.Output ? _config.OutputVolumes : _config.InputVolumes;
    }

    private void SaveSoon()
    {
        if (!_disposed)
        {
            _saveDebounceTimer.Change(SaveDebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void SaveNow()
    {
        if (!_disposed)
        {
            _ = Task.Run(SaveSafelyAsync);
        }
    }

    private async Task SaveSafelyAsync()
    {
        try
        {
            await _store.SaveAsync(_config);
        }
        catch (Exception exception)
        {
            AppLogger.Error(exception, "Could not save app config.");
        }
    }

    private void SaveNowBlocking()
    {
        try
        {
            _store.SaveAsync(_config).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            AppLogger.Error(exception, "Could not save app config.");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        StopAudioRuntime();
        _audio.DefaultDeviceChanged -= OnDefaultDeviceChanged;
        _audio.VolumeChanged -= OnVolumeChanged;
        _volumeTimer.Dispose();
        _deviceRefreshTimer.Dispose();
        _refreshDebounceTimer.Dispose();
        _saveDebounceTimer.Dispose();
        SaveNowBlocking();
    }
}
