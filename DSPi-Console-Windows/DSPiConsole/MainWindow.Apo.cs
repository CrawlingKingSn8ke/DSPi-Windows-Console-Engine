using DspiSetup;
using Dspi.Audio.Windows;
using Microsoft.UI.Xaml.Controls;
using Dspi.Software;
using DSPiConsole.Models;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Microsoft.UI.Xaml;

namespace DSPiConsole;

public sealed partial class MainWindow
{
    private bool _openingWindowsOutput;
    private bool _restoringWindowsApoLinks;

    private async void OnWindowsApoClick(object sender, RoutedEventArgs e)
    {
        if (_softwareFileBusy || _openingWindowsOutput) return;
        _openingWindowsOutput = true;
        try
        {
            var outputs = await Task.Run(() =>
            {
                var setupOutputs = OutputSetup.ListOutputs().ToDictionary(output => output.EndpointId, StringComparer.OrdinalIgnoreCase);
                using var discovery = new LiveAudioService();
                return discovery.EnumerateRenderDevices()
                    .Where(device => !device.IsVirtualCable)
                    .Select(device => (Device: device, Setup: setupOutputs.GetValueOrDefault(device.Id),
                        Attached: IsWindowsDspAttached(device.Id)))
                    .ToArray();
            });
            if (_closedWindowsControls) return;
            var picker = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight };
            string? current = ViewModel.Device.WindowsApoEndpointId;
            foreach (var (endpoint, setup, attached) in outputs)
            {
                if (attached)
                {
                    var item = new ToggleMenuFlyoutItem
                    {
                        Text = endpoint.Name,
                        IsChecked = string.Equals(current, endpoint.Id, StringComparison.OrdinalIgnoreCase)
                    };
                    item.Click += async (_, _) =>
                    {
                        if (ViewModel.Device.WindowsApoEndpointId == endpoint.Id) return;
                        try { await ConnectWindowsApoAsync(endpoint); }
                        catch (Exception ex) { await ShowErrorDialog(ex.Message); }
                    };
                    picker.Items.Add(item);
                }
                else
                {
                    bool canAttach = setup?.CanAttach == true;
                    var item = new MenuFlyoutItem
                    {
                        Text = canAttach ? $"Attach DSPi to {endpoint.Name}" : $"{endpoint.Name} (attachment unavailable)",
                        IsEnabled = canAttach
                    };
                    if (setup != null && canAttach)
                    {
                        item.Click += async (_, _) =>
                        {
                            try { await AttachWindowsApoAsync(setup); }
                            catch (Exception ex) { await ShowErrorDialog(ex.Message); }
                        };
                    }
                    picker.Items.Add(item);
                }
            }
            if (outputs.Length == 0)
                picker.Items.Add(new MenuFlyoutItem { Text = "No Windows playback devices", IsEnabled = false });
            if (current != null)
            {
                picker.Items.Add(new MenuFlyoutSeparator());
                var disconnect = new MenuFlyoutItem { Text = "Disconnect controls" };
                disconnect.Click += async (_, _) =>
                {
                    try { await DisconnectWindowsApoAsync(); }
                    catch (Exception ex) { await ShowErrorDialog(ex.Message); }
                };
                picker.Items.Add(disconnect);
            }
            var detach = new MenuFlyoutSubItem { Text = "Detach DSPi from Output..." };
            foreach (var (endpoint, _, attached) in outputs)
            {
                if (!attached) continue;
                var item = new MenuFlyoutItem { Text = endpoint.Name };
                item.Click += async (_, _) =>
                {
                    try { await DetachWindowsApoAsync(endpoint.Id); }
                    catch (Exception ex) { await ShowErrorDialog(ex.Message); }
                };
                detach.Items.Add(item);
            }
            if (detach.Items.Count > 0)
            {
                if (current == null) picker.Items.Add(new MenuFlyoutSeparator());
                picker.Items.Add(detach);
            }
            picker.ShowAt(DeviceSelectorBtn);
        }
        catch (Exception ex)
        {
            if (!_closedWindowsControls) await ShowErrorDialog(ex.Message);
        }
        finally { _openingWindowsOutput = false; }
    }
    private async Task AttachWindowsApoAsync(Output output)
    {
        if (_softwareFileBusy) return;
        if (!OutputSetup.InstallerAvailable)
            throw new InvalidOperationException("Use the complete x64 DSPi download to attach an output.");

        bool enableAudioTesting = OutputSetup.NeedsAudioTestingApproval;
        string message = enableAudioTesting
            ? "DSPi needs Windows audio testing for its unsigned effect. This reduces protection across all playback outputs. DSPi saves the original setting and restores it after the last managed attachment is removed."
            : "Windows asks for administrator approval and briefly interrupts playback.";
        var confirmation = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Attach DSPi to {output.Name}?",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = enableAudioTesting ? "Enable audio testing and attach" : "Attach DSPi",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

        _softwareFileBusy = true;
        ApplySoftwareMenuState();
        try
        {
            await ViewModel.FlushSoftwareWritesAsync();
            CloseWindowsTestSignals();
            await OutputSetup.ApplyAsync(output, SetWindowsProcessingNotice, enableAudioTesting);
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
        var endpoint = await Task.Run(() => FindStartupOutput(output.EndpointId));
        if (endpoint == null)
            throw new InvalidOperationException("DSPi attached, but the output did not return after restarting Windows Audio. Reopen the output picker to connect.");
        await ConnectWindowsApoAsync(endpoint);
    }

    private async void RestoreWindowsApoConnectionOnStartup(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        Activated -= RestoreWindowsApoConnectionOnStartup;
        string? setupOutput = Environment.GetEnvironmentVariable("DSPI_SETUP_OUTPUT");
        string? remembered = setupOutput ?? AppSettings.Instance.WindowsApoControlEndpointId;
        if (remembered == string.Empty) return;

        try
        {
            var endpoint = await Task.Run(() => FindStartupOutput(remembered));
            if (endpoint != null && !ViewModel.Device.IsWindowsApo && !_softwareFileBusy)
                await ConnectWindowsApoAsync(endpoint, useStartupPreset: setupOutput == null, rememberOutput: setupOutput == null);
        }
        catch (Exception ex)
        {
            // An unplugged, changed or unavailable endpoint should leave the
            // playback chooser usable. Preserve the preference for next launch.
            System.Diagnostics.Debug.WriteLine($"DSPi could not restore playback controls: {ex.Message}");
        }
    }

    private static AudioEndpointInfo? FindStartupOutput(string? remembered)
    {
        if (remembered == null)
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            remembered = device.ID;
        }

        if (!IsWindowsDspAttached(remembered)) return null;
        using var discovery = new LiveAudioService();
        return discovery.EnumerateRenderDevices().FirstOrDefault(output =>
            !output.IsVirtualCable && string.Equals(output.Id, remembered, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWindowsDspAttached(string endpointId)
    {
        const string endpointPrefix = "{0.0.0.00000000}.";
        if (!endpointId.StartsWith(endpointPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(endpointId[endpointPrefix.Length..], "B", out var id)) return false;
        string effectsKey = $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{id:B}\FxProperties";
        var effect = Registry.GetValue(effectsKey, "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7", null) as string;
        const string clsid = "{e53f75ef-9901-4ec4-a8d1-2fe2606a7431}";
        var composite = Registry.GetValue(effectsKey, "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15", null) as string[];
        return string.Equals(effect, clsid, StringComparison.OrdinalIgnoreCase) ||
            composite?.Contains(clsid, StringComparer.OrdinalIgnoreCase) == true;
    }

    private static void RememberWindowsApoOutput(string endpointId)
    {
        var settings = AppSettings.Instance;
        if (settings.WindowsApoControlEndpointId == endpointId) return;
        settings.WindowsApoControlEndpointId = endpointId;
        settings.Save();
    }

    private Task ConnectWindowsApoAsync(AudioEndpointInfo endpoint) => ConnectWindowsApoAsync(endpoint, useStartupPreset: false);

    private async Task ConnectWindowsApoAsync(AudioEndpointInfo endpoint, bool useStartupPreset, bool rememberOutput = true)
    {
        if (_softwareFileBusy) throw new InvalidOperationException("Wait for the current operation to finish.");
        _softwareFileBusy = true; ApplySoftwareMenuState();
        try
        {
            CloseWindowsTestSignals();
            await StopLiveAudioAsync();
            await ViewModel.PrepareForBackendSwitchAsync();
            EngineConfiguration? startup = useStartupPreset ? GetStartupPreset(endpoint) : null;
            var names = await Task.Run(() => ReadSpeakerNames(endpoint));
            ViewModel.SetWindowsSpeakerNames(names);
            await Task.Run(() => ViewModel.Device.UseWindowsApo(endpoint.Id, endpoint.Name, endpoint.SampleRate,
                endpoint.Channels, startupPreset: startup));
            if (rememberOutput) RememberWindowsApoOutput(endpoint.Id);
            RestorePairLinksForDevice();
            RefreshWindowsVolume();
            RefreshWindowsPresetComboBox();
        }
        finally { _softwareFileBusy = false; ApplySoftwareMenuState(); }
    }
    private static string[] ReadSpeakerNames(AudioEndpointInfo endpoint)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpoint.Id);
        using var audio = device.AudioClient;
        var format = audio.MixFormat;
        return format.Channels == endpoint.Channels
            ? WindowsSpeakerLayout.GetLabels(format)
            : WindowsSpeakerLayout.GetLabels(endpoint.Channels, 0);
    }

    private async Task DisconnectWindowsApoAsync()
    {
        if (!ViewModel.Device.IsWindowsApo || _softwareFileBusy) return;
        _softwareFileBusy = true; ApplySoftwareMenuState();
        try
        {
            CloseWindowsTestSignals();
            await ViewModel.PrepareForBackendSwitchAsync();
            ViewModel.Device.Disconnect();
            ViewModel.SetWindowsSpeakerNames([]);
            RememberWindowsApoOutput(string.Empty);
            RefreshWindowsPresetComboBox();
        }
        finally { _softwareFileBusy = false; ApplySoftwareMenuState(); }
    }
}
