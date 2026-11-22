using DspiSetup;
using System.Diagnostics;
using System.Text;
using DSPiConsole.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.CoreAudioApi;

namespace DSPiConsole;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _windowsVolumeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private MMDeviceEnumerator? _windowsVolumeEnumerator;
    private MMDevice? _windowsVolumeDevice;
    private string? _windowsVolumeEndpoint;
    private float _windowsPlaybackMeterGain = 1;
    private readonly DispatcherTimer _windowsStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _readingWindowsStatus;
    private bool _closedWindowsControls;
    private TestSignalsWindow? _windowsSignals;


    private void InitializeWindowsConsole()
    {
        Activated += RestoreWindowsApoConnectionOnStartup;
        InitializeNamedPresets();
        // Named elements still used by the shared controller are detached from
        // the menu so their commands and keyboard shortcuts cannot be invoked.
        foreach (var item in new MenuFlyoutItemBase[] { SettingsMenuItem, SaveMasterVolumeMenuItem, SaveOutputConfigMenuItem })
            MainMenu.Items.Remove(item);
        foreach (var item in MainMenu.Items.OfType<MenuFlyoutItem>().Where(item => item.Text == "Save Preset").ToArray())
            MainMenu.Items.Remove(item);
        var import = MainMenu.Items.OfType<MenuFlyoutSubItem>().Single(item => item.Text == "Import");
        var export = MainMenu.Items.OfType<MenuFlyoutSubItem>().Single(item => item.Text == "Export");
        import.Items.Remove(ImportPresetMenuItem);
        export.Items.Remove(ExportPresetMenuItem);
        SoftwareMenu.Items.Remove(CaptureAudioMenuItem);
        SoftwareMenu.Items.Remove(ProcessSoftwareWavMenuItem);
        SidebarSettingsBtn.Visibility = Visibility.Collapsed;
        SidebarStatsBtn.Visibility = Visibility.Collapsed;
        Cpu0Meter.Visibility = Visibility.Collapsed;
        SidebarVolumeModeLabel.IsHitTestVisible = false;
        SidebarVolumeModeLabel.Text = "Windows volume";
        SetSidebarSliderTint((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]);
        _updatingMasterVolumeSlider = true;
        try
        {
            MasterVolumeSlider.Maximum = 100;
            MasterVolumeSlider.SmallChange = 1;
            MasterVolumeSlider.LargeChange = 10;
        }
        finally { _updatingMasterVolumeSlider = false; }
        RefreshWindowsVolume();
        _windowsVolumeTimer.Tick += (_, _) => RefreshWindowsVolume();
        _windowsVolumeTimer.Start();
        _windowsStatusTimer.Tick += async (_, _) => await UpdateWindowsProcessingNotice();
        _windowsStatusTimer.Start();
        Closed += (_, _) =>
        {
            _closedWindowsControls = true;
            CloseWindowsTestSignals();
            _windowsStatusTimer.Stop();
            _windowsVolumeTimer.Stop();
            CloseWindowsVolumeDevice();
            _windowsVolumeEnumerator?.Dispose();
        };
    }

    private async Task UpdateWindowsProcessingNotice()
    {
        if (_readingWindowsStatus || _closedWindowsControls || _softwareFileBusy) return;
        if (ViewModel.Device.WindowsApoEndpointId is not { } endpoint)
        {
            SetWindowsProcessingNotice(null);
            return;
        }
        ulong revision = ViewModel.Device.WindowsApoDesiredRevision;
        _readingWindowsStatus = true;
        try
        {
            var statuses = await Task.Run(ViewModel.Device.GetWindowsApoStatuses);
            if (_closedWindowsControls || _softwareFileBusy || ViewModel.Device.WindowsApoEndpointId != endpoint ||
                ViewModel.Device.WindowsApoDesiredRevision != revision) return;
            bool pending = statuses.Any(s => s.IsFresh && s.Status != 5 && s.PendingRestartRevision == revision);
            SetWindowsProcessingNotice(pending ? "DSP settings saved. Restart playback to apply the lookahead change." : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_closedWindowsControls) SetWindowsProcessingNotice(null);
        }
        finally { _readingWindowsStatus = false; }
    }

    private void SetWindowsProcessingNotice(string? text)
    {
        WindowsProcessingNotice.Text = text ?? "";
        WindowsProcessingNotice.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnWindowsTestSignalsClick(object sender, RoutedEventArgs e)
    {
        if (_softwareFileBusy || !ViewModel.Device.IsWindowsApo ||
            ViewModel.WindowsSpeakerNames.Count == 0) return;
        if (_windowsSignals == null)
        {
            _windowsSignals = new TestSignalsWindow(ViewModel);
            _windowsSignals.Closed += (_, _) => _windowsSignals = null;
        }
        _windowsSignals.Activate();
    }

    private void CloseWindowsTestSignals() => _windowsSignals?.Close();

    private void CloseWindowsVolumeDevice()
    {
        _windowsVolumeDevice?.Dispose();
        _windowsVolumeDevice = null;
        _windowsVolumeEndpoint = null;
        _windowsPlaybackMeterGain = 1;
    }

    private void RefreshWindowsVolume()
    {
        _updatingMasterVolumeSlider = true;
        try
        {
            string? endpoint = ViewModel.Device.WindowsApoEndpointId;
            if (endpoint != _windowsVolumeEndpoint)
            {
                CloseWindowsVolumeDevice();
                if (endpoint != null)
                {
                    _windowsVolumeEnumerator ??= new MMDeviceEnumerator();
                    _windowsVolumeDevice = _windowsVolumeEnumerator.GetDevice(endpoint);
                    _windowsVolumeEndpoint = endpoint;
                }
            }
            if (_windowsVolumeDevice?.State == DeviceState.Active)
            {
                var volume = _windowsVolumeDevice.AudioEndpointVolume;
                bool muted = volume.Mute;
                float scalar = volume.MasterVolumeLevelScalar;
                var percent = muted ? 0 : Math.Round(scalar * 100);
                UpdateWindowsMeterGain(volume, scalar, muted);
                MasterVolumeSlider.Value = percent;
                MasterVolumeValueText.Text = $"{percent}%";
                MasterVolumeSlider.IsEnabled = true;
                return;
            }
        }
        catch (System.Runtime.InteropServices.COMException) { CloseWindowsVolumeDevice(); }
        finally { _updatingMasterVolumeSlider = false; }

        MasterVolumeSlider.IsEnabled = false;
        MasterVolumeValueText.Text = "—";
    }

    private void UpdateWindowsMeterGain(AudioEndpointVolume volume, float scalar, bool muted)
    {
        float gain = WindowsPlaybackMeter.Gain(muted || scalar <= 0 ? 0 : volume.MasterVolumeLevel, scalar, muted);
        if (_windowsPlaybackMeterGain == gain) return;
        _windowsPlaybackMeterGain = gain;
        UpdateMeters();
    }

    private void SetWindowsVolume(double percent)
    {
        if (_windowsVolumeDevice == null || _windowsVolumeEndpoint != ViewModel.Device.WindowsApoEndpointId) return;
        try
        {
            var volume = _windowsVolumeDevice.AudioEndpointVolume;
            volume.MasterVolumeLevelScalar = (float)Math.Clamp(percent / 100, 0, 1);
            if (percent > 0 && volume.Mute)
                volume.Mute = false;
            UpdateWindowsMeterGain(volume, volume.MasterVolumeLevelScalar, volume.Mute);
            MasterVolumeValueText.Text = $"{percent:F0}%";
        }
        catch (System.Runtime.InteropServices.COMException) { RefreshWindowsVolume(); }
    }

    private async void OnSpectrumSettingsClick(object sender, RoutedEventArgs e)
    {
        var settings = new Settings.Pages.SpectrumSettingsPanel(ViewModel);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Spectrum Settings",
            Content = new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 535 },
            CloseButtonText = "Close"
        };
        await dialog.ShowAsync();
    }

    private async void OnDetachWindowsApoClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Device.WindowsApoEndpointId is not { } id)
        {
            OnWindowsApoClick(sender, e);
            return;
        }
        try { await DetachWindowsApoAsync(id); }
        catch (Exception ex) { await ShowErrorDialog(ex.Message); }
    }

    private async Task DetachWindowsApoAsync(string endpointId, XamlRoot? owner = null)
    {
        if (_softwareFileBusy) return;
        var restoreProtection = new CheckBox { Content = "Restore protected audio if this is the last DSPi output" };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "Windows asks for administrator approval and briefly interrupts playback. Restoring protected audio applies to all outputs.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(restoreProtection);
        var confirmation = new ContentDialog
        {
            XamlRoot = owner ?? Content.XamlRoot,
            Title = "Detach DSPi from this output?",
            Content = content,
            PrimaryButtonText = "Detach DSPi",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

        _softwareFileBusy = true;
        ApplySoftwareMenuState();
        int exitCode;
        try
        {
            await ViewModel.FlushSoftwareWritesAsync();
            CloseWindowsTestSignals();
            exitCode = await DetachWindowsEffectAsync(endpointId, restoreProtection.IsChecked == true);
            if (exitCode == 2) throw new InvalidOperationException("No active DSPi installation with protected recovery was found for this output.");
            if (exitCode == 3) throw new InvalidOperationException("Windows detached DSPi but cleanup still needs attention. Run protected recovery to finish.");
            if (exitCode is not (0 or 4 or 5 or 6)) throw new InvalidOperationException("DSPi removal could not be verified. Check the protected recovery status.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return; }
        finally
        {
            _softwareFileBusy = false;
            if (!_closedWindowsControls)
            {
                SetWindowsProcessingNotice(null);
                ApplySoftwareMenuState();
            }
        }
        if (_closedWindowsControls) return;
        if (ViewModel.Device.WindowsApoEndpointId == endpointId)
            await DisconnectWindowsApoAsync();

        string? notice = exitCode switch
        {
            4 => "Another output still uses DSPi, so the audio test setting remains on.",
            5 => "DSPi was detached, but secure Windows audio protection could not be confirmed.",
            6 => "DSPi was detached. Restart Windows to apply protected audio.",
            _ => null
        };
        if (notice != null)
        {
            var outcome = new ContentDialog
            {
                XamlRoot = owner ?? Content.XamlRoot,
                Title = "DSPi detached",
                Content = notice,
                CloseButtonText = "OK"
            };
            await outcome.ShowAsync();
        }
    }

    private async Task<int> DetachWindowsEffectAsync(string endpointId, bool restoreProtection)
    {
        var output = await Task.Run(() => OutputSetup.ListOutputs().SingleOrDefault(candidate =>
            string.Equals(candidate.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)));
        if (output is { Session: not null })
            return await OutputSetup.RemoveAsync(output, restoreProtection, SetWindowsProcessingNotice);

        string host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (!File.Exists(host))
            throw new InvalidOperationException("PowerShell 7 is required for DSPi recovery.");
        using var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("DSPiConsole.DetachWindowsApo.ps1")
            ?? throw new InvalidOperationException("The DSPi recovery command is missing.");
        using var reader = new StreamReader(resource);
        string selection = Convert.ToBase64String(Encoding.UTF8.GetBytes(endpointId));
        using var protection = typeof(MainWindow).Assembly.GetManifestResourceStream("DspiSetup.AudioProtection.psm1")
            ?? throw new InvalidOperationException("The Windows audio protection command is missing.");
        using var protectionReader = new StreamReader(protection);
        string module = Convert.ToBase64String(Encoding.UTF8.GetBytes(await protectionReader.ReadToEndAsync()));
        string script = "$endpoint=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + selection + "'))\n" +
            "$restoreProtection=$" + (restoreProtection ? "true" : "false") + "\n" +
            "New-Module -Name DspiAudioProtection -ScriptBlock ([ScriptBlock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
            module + "')))) | Import-Module\n" + await reader.ReadToEndAsync();
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(host)!,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not open protected DSPi recovery.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}

