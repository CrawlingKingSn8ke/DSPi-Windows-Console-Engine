using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DspiSetup;

internal sealed record Output(Guid Id, string Name, bool CanAttach, bool Attached, string? Session)
{
    public string EndpointId => "{0.0.0.00000000}." + Id.ToString("B");
    public override string ToString() => Name;
}

internal sealed record Receipt(string Endpoint, string SessionId, string Operation, bool Installed, bool RolledBack,
    string? Error, string? RecoveryError);

internal static class OutputSetup
{
    private static string ConsoleDirectory => File.Exists(Path.Combine(AppContext.BaseDirectory, "DSPiConsole.exe"))
        ? AppContext.BaseDirectory : Path.Combine(AppContext.BaseDirectory, "Console");
    private const string Render = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string FxProperty = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},";
    private const string Effect = FxProperty + "7";
    private const string CompositeEffect = FxProperty + "15";
    private static readonly string[] ModernEffectProperties = ["5", "6", "7", "13", "14", "15"];
    private const string Binding = "{67fe7ad1-1232-4c16-94ce-83a4f63be981},1";
    private const string DspiClsid = "{e53f75ef-9901-4ec4-a8d1-2fe2606a7431}";
    private const string Speaker = "{dff21ce1-f70f-11d0-b917-00a0c9223196}";
    private static readonly string Reports = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DSPi", "APO");
    private static readonly string Shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");
    private static readonly string Archive = Path.Combine(AppContext.BaseDirectory, "PowerShell-runtime.zip");
    internal static bool InstallerAvailable => HasPayload && File.Exists(Archive) &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64 && RuntimeInformation.OSArchitecture == Architecture.X64;
    private static bool HasPayload => typeof(OutputSetup).Assembly.GetManifestResourceInfo("DspiSetup.Payload.Apo.dll") != null;
    internal static bool NeedsAudioTestingApproval => !AudioTestingEnabled;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    internal static IReadOnlyList<Output> ListOutputs()
    {
        using var root = Registry.LocalMachine.OpenSubKey(Render);
        if (root == null) return [];
        var sessions = ReadSessions();
        var outputs = new List<Output>();
        foreach (string name in root.GetSubKeyNames())
        {
            if (!Guid.TryParseExact(name, "B", out Guid id)) continue;
            using var key = root.OpenSubKey(name);
            if ((key?.GetValue("DeviceState") as int?) != 1) continue;
            using var properties = key!.OpenSubKey("Properties");
            if (properties == null) continue;
            string label = properties.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string ?? "Playback device";
            string? adapter = properties.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;
            string display = string.IsNullOrWhiteSpace(adapter) ? label : $"{label} ({adapter})";
            using var effects = key.OpenSubKey("FxProperties");
            bool attached = string.Equals(effects?.GetValue(Effect) as string, DspiClsid, StringComparison.OrdinalIgnoreCase)
                || (effects?.GetValue(CompositeEffect) as string[])?.Contains(DspiClsid, StringComparer.OrdinalIgnoreCase) == true;
            bool speaker = string.Equals(properties.GetValue("{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},8") as string, Speaker, StringComparison.OrdinalIgnoreCase);
            string endpoint = "{0.0.0.00000000}." + id.ToString("B");
            string[] names = effects?.GetValueNames() ?? [];
            bool legacy = names.Contains(FxProperty + "1", StringComparer.OrdinalIgnoreCase) ||
                names.Contains(FxProperty + "2", StringComparer.OrdinalIgnoreCase);
            bool legacyOnly = legacy && !ModernEffectProperties.Any(property =>
                names.Contains(FxProperty + property, StringComparer.OrdinalIgnoreCase));
            outputs.Add(new Output(id, display, speaker && !legacyOnly && effects?.GetValue(Binding) == null, attached,
                attached ? sessions.GetValueOrDefault(endpoint) : null));
        }
        return outputs.OrderBy(output => output.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static Dictionary<string, string> ReadSessions()
    {
        var sessions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(Reports)) return sessions;
        foreach (string file in Directory.EnumerateFiles(Reports, "setup-*.json").OrderBy(File.GetLastWriteTimeUtc))
        {
            try
            {
                var result = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(file), Json);
                if (result is { Operation: "Install", Installed: true } &&
                    Guid.TryParseExact(result.SessionId, "N", out _))
                    sessions[result.Endpoint] = result.SessionId;
                else if (result is { Operation: "Remove", RolledBack: true } &&
                    sessions.GetValueOrDefault(result.Endpoint) == result.SessionId)
                    sessions.Remove(result.Endpoint);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return sessions;
    }

    internal static bool AudioTestingEnabled => Registry.GetValue(
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio", "DisableProtectedAudioDG", null) is 1;

    internal static async Task ApplyAsync(Output output, Action<string>? progress = null, bool enableAudioTesting = false)
    {
        if (!output.CanAttach || output.Attached)
            throw new InvalidOperationException("Choose an active speaker output with a supported effects configuration.");
        if (!File.Exists(Path.Combine(ConsoleDirectory, "DSPiConsole.exe")))
            throw new InvalidOperationException("DSPi Console is missing from this setup folder.");
        await RunAsync(output, "Install", Guid.NewGuid().ToString("N"), progress: progress, enableAudioTesting: enableAudioTesting);
    }

    internal static async Task<int> RemoveAsync(Output output, bool restoreProtection, Action<string>? progress = null)
    {
        if (!output.Attached || output.Session == null)
            throw new InvalidOperationException("This output has no attachment made by this installer.");
        return await RunAsync(output, "Remove", output.Session, restoreProtection, progress);
    }

    private static async Task<int> RunAsync(Output output, string operation, string session, bool restoreProtection = false,
        Action<string>? progress = null, bool enableAudioTesting = false)
    {
        if (!InstallerAvailable || !File.Exists(Shell))
            throw new InvalidOperationException("Use the complete x64 DSPi download on x64 Windows. Keep PowerShell-runtime.zip next to the Console executable.");
        if (operation == "Install" && !AudioTestingEnabled && !enableAudioTesting)
            throw new InvalidOperationException("Open DSPi Setup to approve enabling Windows audio testing before attaching DSPi.");

        string operationId = Guid.NewGuid().ToString("N");
        string path = Directory.CreateTempSubdirectory("dspi-setup-").FullName;
        try
        {
            byte[] module = Resource("ProtectedStorage.psm1");
            string effects = Encoding.UTF8.GetString(Resource("EndpointEffects.ps1")).TrimStart('\uFEFF');
            byte[] worker = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Resource("Worker.ps1"))
                .Replace("# Insert endpoint effect functions", effects, StringComparison.Ordinal));
            byte[] admin = Resource("Admin.ps1");
            byte[] shared = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Resource("SharedComponent.psm1")) + "\n" +
                Encoding.UTF8.GetString(Resource("AudioProtection.psm1")).TrimStart('\uFEFF') + "\n" + effects +
                "\nExport-ModuleMember -Function New-DspiEffectPlan");
            byte[] apo = Resource("Payload.Apo.dll");
            File.WriteAllBytes(Path.Combine(path, "ProtectedStorage.psm1"), module);
            File.WriteAllBytes(Path.Combine(path, "Worker.ps1"), worker);
            File.WriteAllBytes(Path.Combine(path, "SharedComponent.psm1"), shared);
            File.WriteAllBytes(Path.Combine(path, "dspi-apo.dll"), apo);
            File.WriteAllBytes(Path.Combine(path, "Admin.ps1"), admin);
            using var identity = WindowsIdentity.GetCurrent();
            string user = identity.User?.Value ?? throw new InvalidOperationException("The active Windows user could not be identified.");
            string script = $"$Operation={Quote(operation)}\n$OperationId='{operationId}'\n$SessionId={Quote(session)}\n" +
                $"$RestoreProtection='{(restoreProtection ? 1 : 0)}'\n$EnableAudioTesting='{(enableAudioTesting ? 1 : 0)}'\n$EndpointGuid='{output.Id:B}'\n$Payload={Quote(path)}\n$ExpectedUser={Quote(user)}\n" +
                $"$ModuleHash='{Convert.ToHexString(SHA256.HashData(module))}'\n$WorkerHash='{Convert.ToHexString(SHA256.HashData(worker))}'\n" +
                $"$ArchivePath={Quote(Archive)}\n$AdminHash='{Convert.ToHexString(SHA256.HashData(admin))}'\n" +
                $"$SharedHash='{Convert.ToHexString(SHA256.HashData(shared))}'\n$ApoHash='{Convert.ToHexString(SHA256.HashData(apo))}'\n" +
                Encoding.UTF8.GetString(Resource("Bootstrap.ps1")).TrimStart('\uFEFF');
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var start = new ProcessStartInfo(Shell, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            progress?.Invoke("Waiting for Windows administrator approval…");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the administrator helper.");
            progress?.Invoke(operation == "Install"
                ? "Windows approved Setup. Preparing the runtime and attaching DSPi…"
                : "Windows approved Setup. Removing DSPi…");
            using var timeLimit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(timeLimit.Token); }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("Setup is still running. Wait for Windows to finish before using this output again.");
            }

            string resultPath = Path.Combine(Reports, $"setup-{operationId}.json");
            if (!File.Exists(resultPath))
                throw new InvalidOperationException(ExplainMissingReceipt(process.ExitCode));
            var report = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(resultPath), Json)
                ?? throw new InvalidOperationException("Windows returned an unreadable setup result.");
            if (report.Endpoint != output.EndpointId || (operation == "Install" ? !report.Installed : !report.RolledBack))
                throw new InvalidOperationException(string.Join("\n", new[] { report.Error ?? "Setup could not be confirmed.", report.RecoveryError }.OfType<string>()));
            return process.ExitCode;
        }
        finally
        {
            foreach (string file in new[] { "ProtectedStorage.psm1", "Worker.ps1", "SharedComponent.psm1", "dspi-apo.dll", "Admin.ps1" })
                File.Delete(Path.Combine(path, file));
            Directory.Delete(path, recursive: false);
        }
    }

    internal static async Task OpenConsoleAsync(Output output)
    {
        string executable = Path.Combine(ConsoleDirectory, "DSPiConsole.exe");
        int? lastExitCode = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = ConsoleDirectory };
            start.Environment["DSPI_SETUP_OUTPUT"] = output.EndpointId;
            using var console = Process.Start(start) ?? throw new InvalidOperationException("DSPi Console could not start.");
            for (int poll = 0; poll < 55; poll++)
            {
                await Task.Delay(120);
                console.Refresh();
                if (console.HasExited) break;
                if (poll >= 15 && console.MainWindowHandle != IntPtr.Zero) return;
            }
            if (!console.HasExited) break;
            lastExitCode = console.ExitCode;
        }
        string detail = lastExitCode.HasValue ? $" The Console exited with code 0x{lastExitCode.Value:X8}." :
            " The Console did not show a window before the timeout.";
        throw new InvalidOperationException("DSPi is attached." + detail + " Try Open Console or launch Console\\DSPiConsole.exe directly.");
    }

    private static string ExplainMissingReceipt(int code) => code switch
    {
        20 => "Setup could not read or extract PowerShell-runtime.zip. Extract the entire DSPi download to a local folder and try again.",
        21 => "PowerShell-runtime.zip failed its checksum check. Download a fresh copy of the DSPi ZIP.",
        22 => "Windows could not verify the bundled PowerShell signature. Check Windows Update and the system date before retrying.",
        23 => "Windows could not start the bundled PowerShell runtime.",
        24 => "The administrator helper did not finish in time. Wait before trying again.",
        25 => "The administrator helper could not create or verify its protected setup folder.",
        _ => $"Setup stopped before it could confirm the change (administrator helper exit code {code})."
    };

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static byte[] Resource(string name)
    {
        using var stream = typeof(OutputSetup).Assembly.GetManifestResourceStream("DspiSetup." + name)
            ?? throw new InvalidOperationException("An embedded DSPi installer component is missing.");
        using var data = new MemoryStream();
        stream.CopyTo(data);
        return data.ToArray();
    }
}
