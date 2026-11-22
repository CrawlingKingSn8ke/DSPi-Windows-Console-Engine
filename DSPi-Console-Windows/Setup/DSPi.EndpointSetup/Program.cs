using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DspiSetup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && Guid.TryParseExact(args[1], "B", out Guid id))
        {
            try
            {
                var output = OutputSetup.ListOutputs().Single(item => item.Id == id);
                if (args[0] == "--apply")
                {
                    OutputSetup.ApplyAsync(output).GetAwaiter().GetResult();
                    OutputSetup.OpenConsoleAsync(output).GetAwaiter().GetResult();
                    return 0;
                }
                if (args[0] == "--open" && output.Attached)
                {
                    OutputSetup.OpenConsoleAsync(output).GetAwaiter().GetResult();
                    return 0;
                }
                if (args[0] is "--remove" or "--remove-and-restore-audio")
                {
                    if (output.Session == null) return 2;
                    return OutputSetup.RemoveAsync(output, args[0] == "--remove-and-restore-audio").GetAwaiter().GetResult();
                }
                return 2;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "dspi-endpoint-setup-error.txt"), ex.Message);
                return 1;
            }
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupWindow());
        return 0;
    }
}

internal sealed class SetupWindow : Form
{
    private readonly ComboBox outputs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly Label status = new() { AutoSize = false, Height = 70, Dock = DockStyle.Top, MaximumSize = new Size(480, 0) };
    private readonly Button install = new() { Text = "Attach and open Console", AutoSize = true };
    private readonly Button remove = new() { Text = "Remove DSPi", AutoSize = true };
    private readonly Button refresh = new() { Text = "Refresh outputs", AutoSize = true };
    private bool busy;

    internal SetupWindow()
    {
        Text = "DSPi Output Setup";
        ClientSize = new Size(525, 220);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 10);
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, WrapContents = false };
        actions.Controls.AddRange([install, remove, refresh]);
        var title = new Label { Text = "Windows playback output", Dock = DockStyle.Top, Height = 30, Font = new Font(Font, FontStyle.Bold) };
        content.Controls.AddRange([actions, status, outputs, title]);
        Controls.Add(content);
        outputs.SelectedIndexChanged += (_, _) => UpdateStatus();
        refresh.Click += (_, _) => RefreshOutputs();
        install.Click += async (_, _) => await RunSelected(removeAttachment: false);
        remove.Click += async (_, _) => await RunSelected(removeAttachment: true);
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        RefreshOutputs();
    }

    private void RefreshOutputs()
    {
        Guid? current = (outputs.SelectedItem as Output)?.Id;
        var choices = OutputSetup.ListOutputs();
        outputs.DataSource = choices.ToList();
        if (current.HasValue)
        {
            int index = choices.ToList().FindIndex(item => item.Id == current.Value);
            if (index >= 0) outputs.SelectedIndex = index;
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var output = outputs.SelectedItem as Output;
        install.Text = output?.Attached == true ? "Open Console" : "Attach and open Console";
        install.Enabled = !busy && (output?.CanAttach == true || output?.Attached == true);
        remove.Enabled = !busy && output?.Session != null;
        refresh.Enabled = outputs.Enabled = !busy;
        if (!busy)
            status.Text = output switch
            {
                null => "No active Windows playback outputs found.",
                { Attached: true } => "DSPi is attached to this output.",
                { CanAttach: true } => "Setup restarts Windows Audio briefly. Your default playback output stays the same.",
                _ => "This output uses a legacy effects chain or an unsupported speaker type."
            };
    }

    private async Task RunSelected(bool removeAttachment)
    {
        if (busy || outputs.SelectedItem is not Output output) return;
        bool enableAudioTesting = false;
        if (!removeAttachment && !output.Attached && !OutputSetup.AudioTestingEnabled)
        {
            var choice = MessageBox.Show(this,
                "DSPi’s unsigned audio effect requires Windows audio testing. This lowers audio-engine protection for all outputs. " +
                "Setup will save the current setting and restore it after the last DSPi output is removed. Enable audio testing and attach DSPi?",
                "Enable Windows audio testing", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (choice != DialogResult.Yes) return;
            enableAudioTesting = true;
        }
        bool restoreProtection = false;
        if (removeAttachment)
        {
            var choice = MessageBox.Show(this,
                "Setup automatically restores audio testing it enabled after the last removal. Also restore protected audio if you enabled testing manually?",
                "Remove DSPi", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (choice == DialogResult.Cancel) return;
            restoreProtection = choice == DialogResult.Yes;
        }
        busy = true;
        UpdateStatus();
        status.Text = removeAttachment ? "Removing DSPi and restarting Windows Audioâ€¦" :
            output.Attached ? "Opening the Consoleâ€¦" : "Attaching DSPi and restarting Windows Audioâ€¦";
        string result;
        bool failed = false;
        try
        {
            if (removeAttachment)
            {
                int outcome = await OutputSetup.RemoveAsync(output, restoreProtection, message => status.Text = message);
                result = outcome switch
                {
                    0 when restoreProtection => "DSPi was removed. Protected audio is enabled.",
                    0 => "DSPi was removed. Windows Audio restarted.",
                    4 => "DSPi was removed. Another output still uses DSPi, so the audio test setting remains on.",
                    6 => "DSPi was removed. Restart Windows to apply protected audio.",
                    _ => "DSPi was removed. Setup could not confirm protected audio."
                };
            }
            else
            {
                if (!output.Attached) await OutputSetup.ApplyAsync(output, message => status.Text = message, enableAudioTesting);
                status.Text = "DSPi is attached. Opening the Console…";
                await OutputSetup.OpenConsoleAsync(output);
                result = "DSPi Console opened for this output.";
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            failed = true;
            result = "Windows administrator approval was canceled or denied.";
        }
        catch (Exception ex)
        {
            failed = true;
            result = ex.Message;
        }
        busy = false;
        try { RefreshOutputs(); }
        catch (Exception ex)
        {
            failed = true;
            result += "\n\nCould not refresh the playback list. " + ex.Message;
            UpdateStatus();
        }
        status.Text = result;
        if (failed)
            MessageBox.Show(this, result, "DSPi Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}

