# DSPi Windows Console & Engine

DSPi for Windows applies parametric EQ and other DSPi effects to shared-mode audio on a selected Windows playback device. You can use it with a USB DAC or another eligible speaker output. Windows runs the DSP on the PC, so a Pico, virtual audio cable, and Equalizer APO are optional for other workflows and unnecessary for this setup.

## Install and check playback

The Console can enable Windows audio testing for the unsigned x64 build. It asks before changing this machine-wide setting.

1. Download the Windows x64 ZIP from [Releases](https://github.com/CrawlingKingSn8ke/DSPi-Windows-Console-Engine/releases/latest) and extract the entire folder. Keep `DSPiConsole.exe` and the other extracted files together.
2. Open **DSPiConsole.exe**. Click the output picker at the bottom left and choose **Attach DSPi to** followed by your output name. Select an already attached output to reconnect its controls.
3. If prompted, choose **Enable audio testing and attach**, then approve the Windows administrator prompt. The Console saves the current setting, briefly restarts Windows Audio, and connects to the output. The tested install path needs no PC reboot.
4. In Windows Sound settings, route playback to the selected device. Play audio and check the Console meters or spectrum display. For a quick check, try a gentle EQ cut and toggle **Bypass**.

The Console bundles the APO and uses the included PowerShell runtime. It offers active physical speaker outputs with a supported effects configuration. On compatible Windows 10 and 11 drivers, Setup can append DSPi to an existing endpoint effects chain and restore the original chain on removal. Legacy effects-only chains remain ineligible. Closing the Console leaves the APO and last applied settings active.

## What you can adjust

- Independent input and output EQ, input preamp, delay, channel routing, linked output pairs, and crossovers.
- Loudness compensation, headphone crossfeed, volume levelling, limiting, tube modelling, psychoacoustic bass, subharmonics, and stereo upmix where the output layout allows it.
- Named presets, quick recall, per-output startup presets, and preset import/export.
- Real-time spectrum and RTA, plus a signal generator for tones, noise, sweeps, and channel checks.

Start with a low level when using the signal generator. A stereo Windows output carries two physical channels. Separate centre, surround, or subwoofer signals require an output that exposes those channels to Windows.

## How it works

```text
Windows apps → Windows shared audio engine → DSPi APO → selected playback output
                                                ↑
                                    Console settings and presets
```

The APO (Audio Processing Object) loads inside the Windows audio engine for the selected output and uses DSP routines adapted from the [Weeb Labs DSPi firmware](https://github.com/WeebLabs/DSPi). The Console saves settings for that endpoint and reads live processing data. During playback, the APO crossfades compatible settings with a fixed processing delay of about 7 ms. Windows volume is used to calculate the loudness curve without applying the Windows gain a second time. At lower listening levels, the curve adds more low-frequency compensation.

The input meters show the signal entering DSPi. The output meter bars estimate the playback level after Windows master volume and mute. Their clip indicators still show levels above 0 dBFS inside DSPi, before Windows volume. If DSPi’s output clip indicators light up, reduce preamp or DSP gain to leave headroom.

Applications that use exclusive playback or a path outside the Windows shared audio engine can bypass the effect.

## Bypass or remove

- Choose **Bypass** in the Console to temporarily bypass DSPi processing.
- Open the output picker, choose **Detach DSPi from Output**, and select the device. The Console uses the saved recovery record to restore the prior endpoint state and restarts Windows Audio.

If attachment fails, the Console shows the installer error.

If you hear audio but see no meters, confirm that Windows and the Console point to the same output and that the app uses shared playback. If attachment is unavailable for an output, it may use a legacy effects chain or Windows may classify it as something other than an active physical speaker output.

## Enable local audio testing

The Console normally enables this setting after you approve it. Use these commands for older installers or manual setup. The unsigned x64 APO requires a machine-wide Windows audio test setting. Changing it reduces protection for the Windows audio engine across all outputs. Use it on a PC you administer and restore the original setting when you finish testing. Administrator approval is required. [Microsoft APO development notes](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects#disable-use-of-an-embedded-manifest)

1. Open **Windows Terminal** or **Command Prompt** using **Run as administrator**.
2. Run the query below and note the original value. If Windows reports that the value does not exist, record it as **absent**.

```powershell
reg.exe query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio" /v DisableProtectedAudioDG
```

3. Set the value to `1`. If the query already shows `0x1`, continue to installation.

```powershell
reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio" /v DisableProtectedAudioDG /t REG_DWORD /d 1 /f
```

The Console restarts Windows Audio during attachment. If the APO still fails to load after attachment, restart Windows once and check the Console meters again.

## Restore protected audio after testing

Use **Detach DSPi from Output** in the Console. If the installer enabled audio testing, it automatically restores the original setting after you remove the last DSPi output. If you enabled it manually, choose **Restore protected audio** to clear the test override. If another output still has DSPi attached, the audio test setting stays on. After the last detachment, the choice removes the test override and restarts Windows Audio. If Windows cannot restart the audio service, the Console asks you to restart Windows.

Use the manual steps below with older builds or to restore a specific previous registry value.

1. Open the Console output picker, choose **Detach DSPi from Output**, and select the attached output. Repeat for each output you attached.
2. In an administrator terminal, restore the original registry state. If the value was absent, delete just that value.

```powershell
reg.exe delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio" /v DisableProtectedAudioDG /f
```

   If the original value was `0x0`, set it back to zero.

```powershell
reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio" /v DisableProtectedAudioDG /t REG_DWORD /d 0 /f
```

   If it was already `0x1` and you skipped the change, it is already in its original state. For any other pre-existing DWORD, restore the number you recorded.

3. Restart Windows to apply the restored setting.


## Build and test from source

The folders in this directory must sit alongside each other. Use x64 Windows, PowerShell 7, the .NET 8 SDK with the Windows build tools required by WinUI, and Zig 0.15.2.

```powershell
Push-Location .\DSPi-Software-DSP
.\build.ps1 -Compiler 'C:\path\to\zig.exe'
.\scripts\build-apo.ps1 -Compiler 'C:\path\to\zig.exe'
Pop-Location

$runtime = 'C:\path\to\PowerShell-7.6.9-win-x64.zip'
dotnet publish .\DSPi-Console-Windows\DSPiConsole\DSPiConsole.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishProfile= "-p:DspiPowerShellArchive=$runtime" -o .\out\Console
```

The native build generates the Console engine. The APO build runs the Windows COM and DSP harness. To process a sample WAV in the APO test, pass `-SampleFile` to `build-apo.ps1`.

The runtime archive must match the checksum in `Bootstrap.ps1`. If your APO binary has a different hash, update the approved hash in `Admin.ps1` and add it to the trusted list in `Worker.ps1` before publishing. [PowerShell 7.6.9 official downloads](https://github.com/PowerShell/PowerShell/releases/tag/v7.6.9)

The Windows build was tested with the Weeb Labs DSPi USB output and TOPPING DX5 II. The Weeb Labs live check measured the change in the 80 Hz loudness boost as Windows volume moved, and the bundled registration and recovery path was verified in an isolated test.

## Credits and licence

This Windows port uses the open-source [DSPi Console for Windows](https://github.com/WeebLabs/DSPi-Console-Windows) and [DSPi firmware](https://github.com/WeebLabs/DSPi). See the licence files included with the [Console](DSPi-Console-Windows/LICENSE) and [engine](DSPi-Software-DSP/LICENSE) and the engine’s [upstream source record](DSPi-Software-DSP/vendor/dspi/UPSTREAM.md).
