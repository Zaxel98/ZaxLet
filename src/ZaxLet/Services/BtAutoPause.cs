using System;
using System.Threading.Tasks;
using System.Windows;
using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace ZaxLet;

/// <summary>Pauses music when a Bluetooth device disconnects while playing, with a click-to-resume button.</summary>
public sealed class BtAutoPause
{
    public static readonly BtAutoPause I = new();

    readonly BluetoothWatch _bt = new();
    readonly System.Collections.Generic.HashSet<string> _audioNames = new(StringComparer.OrdinalIgnoreCase);   // output endpoint names that have been active: only audio devices trigger pause
    bool _started;

    void Snap()
    {
        try { foreach (var d in AudioOutput.List()) lock (_audioNames) _audioNames.Add(d.Name); }
        catch (Exception ex) { App.Log(ex); }
    }

    bool IsAudioDevice(string btName)
    {
        var n = BtBattery.Clean(btName);
        if (string.IsNullOrWhiteSpace(n)) return false;
        lock (_audioNames) { foreach (var a in _audioNames) if (a.Contains(n, StringComparison.OrdinalIgnoreCase)) return true; }
        return false;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        Snap();
        _bt.Changed += OnDeviceChanged;
        _bt.Start();
    }

    void OnDeviceChanged(string name, bool on)
    {
        if (on) { _ = Task.Delay(3000).ContinueWith(_ => Snap()); return; }   // audio endpoint appears a few seconds after connecting
        if (!Settings.Cur.BtPauseOnLost || !MediaService.I.Playing || !IsAudioDevice(name)) return;   // mouse/keyboard/controller disconnect: ignore
        _ = HandleDisconnectAsync();
    }

    async Task HandleDisconnectAsync()
    {
        try
        {
            if (!await IsBluetoothRadioOnAsync()) return;
            if (!Settings.Cur.BtPauseOnLost || !MediaService.I.Playing) return;
            if (!await MediaService.I.PauseIfPlayingAsync()) return;
            Application.Current?.Dispatcher.BeginInvoke(() =>
                IslandWindow.Instance?.Alert(L.T("Dijeda: headset terputus · klik untuk lanjut"), 8, () => _ = MediaService.I.ResumeAsync(), false));
        }
        catch (Exception ex) { App.Log(ex); }
    }

    static async Task<bool> IsBluetoothRadioOnAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null) return false;
            var radio = await adapter.GetRadioAsync();
            return radio == null || radio.State == RadioState.On;
        }
        catch { return true; }
    }
}
