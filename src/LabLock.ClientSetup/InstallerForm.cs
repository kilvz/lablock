namespace LabLock.ClientSetup;

public sealed class InstallerForm : Form
{
    private readonly string _installDir;
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;
    private readonly Button _installButton;
    private readonly Button _uninstallButton;
    private readonly Button _closeButton;
    private readonly TextBox _serverBox;
    private readonly TextBox _apiKeyBox;
    private readonly TextBox _clientIdBox;

    public InstallerForm(string installDir, InstallerCore.ClientOptions? options = null)
    {
        _installDir = installDir;

        Text = "LabLock Client Installer";
        ClientSize = new Size(460, 340);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var title = new Label
        {
            Text = "LabLock Client Agent",
            Font = new Font("Segoe UI", 17, FontStyle.Bold),
            ForeColor = Color.FromArgb(10, 132, 255),
            AutoSize = true,
            Location = new Point(24, 14)
        };

        var subtitle = new Label
        {
            Text = $"Installs the hidden monitoring agent to:\n{_installDir}\n" +
                   "Runs as an auto-start Windows service (no window).\n" +
                   "Files and service are locked against deletion by users and admins.",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(90, 90, 100),
            AutoSize = true,
            Location = new Point(26, 48)
        };

        _serverBox = MakeField("Server URL", "http://192.168.1.58:5000", options?.ServerUrl ?? InstallerCore.DefaultServerUrl, new Point(26, 120), 356);
        _apiKeyBox = MakeField("Shared API key", "LabLockKey-2026", options?.ApiKey ?? InstallerCore.DefaultApiKey, new Point(26, 156), 356);
        _clientIdBox = MakeField("Client ID (optional)", "auto (machine name)", options?.ClientId ?? "", new Point(26, 192), 356);

        _statusLabel = new Label
        {
            Text = InstallerCore.IsInstalled(_installDir)
                ? "LabLock client is already installed."
                : "Ready to install.",
            Font = new Font("Segoe UI", 9),
            AutoSize = true,
            Location = new Point(26, 226)
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(26, 252),
            Size = new Size(408, 16),
            Minimum = 0,
            Maximum = 100
        };

        _installButton = new Button
        {
            Text = InstallerCore.IsInstalled(_installDir) ? "Reinstall" : "Install",
            Size = new Size(96, 32),
            Location = new Point(108, 286)
        };
        _installButton.Click += async (_, _) => await RunActionAsync(
            (dir, prog, log) => InstallerCore.Install(dir, prog, log, CollectOptions()),
            "Installed");

        _uninstallButton = new Button
        {
            Text = "Uninstall",
            Size = new Size(96, 32),
            Location = new Point(216, 286)
        };
        _uninstallButton.Click += async (_, _) => await RunActionAsync(InstallerCore.Uninstall, "Uninstalled");

        _closeButton = new Button
        {
            Text = "Close",
            Size = new Size(96, 32),
            Location = new Point(324, 286),
            Enabled = false
        };
        _closeButton.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
        {
            title, subtitle, _serverBox, _apiKeyBox, _clientIdBox,
            _statusLabel, _progressBar, _installButton, _uninstallButton, _closeButton
        });
    }

    private TextBox MakeField(string label, string placeholder, string? value, Point location, int width)
    {
        var lbl = new Label
        {
            Text = label,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(70, 70, 80),
            AutoSize = true,
            Location = new Point(location.X, location.Y - 18)
        };
        var box = new TextBox
        {
            Text = value ?? placeholder,
            Font = new Font("Segoe UI", 9),
            Location = location,
            Size = new Size(width, 26)
        };
        Controls.Add(lbl);
        return box;
    }

    private InstallerCore.ClientOptions CollectOptions()
    {
        var server = _serverBox.Text.Trim();
        var key = _apiKeyBox.Text.Trim();
        var id = _clientIdBox.Text.Trim();
        return new InstallerCore.ClientOptions(
            ServerUrl: server.StartsWith("http") ? server : "http://" + server,
            ApiKey: key == "CHANGE_ME_SHARED_KEY" ? null : key,
            ClientId: string.IsNullOrEmpty(id) ? null : id);
    }

    private async Task RunActionAsync(Action<string, Action<int>?, Action<string>?> action, string doneText)
    {
        SetBusy(true);
        _statusLabel.Text = "Working...";
        _progressBar.Value = 3;

        try
        {
            await Task.Run(() => action(
                _installDir,
                p => BeginInvoke(() => _progressBar.Value = p),
                m => BeginInvoke(() => _statusLabel.Text = m)));

            _statusLabel.Text = $"{doneText}.";
            _closeButton.Enabled = true;
            MessageBox.Show(
                $"{doneText}.\n\n" +
                "The client runs as a hidden auto-start Windows service named 'LabLockAgent'.\n" +
                "Users and administrators cannot delete the files or stop/uninstall the\n" +
                "service through normal means - only this installer can (run it again).",
                "LabLock Client Installer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Failed.";
            MessageBox.Show($"Operation failed:\n{ex.Message}", "LabLock Client Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            _closeButton.Enabled = true;
        }
    }

    private void SetBusy(bool busy)
    {
        _installButton.Enabled = !busy;
        _uninstallButton.Enabled = !busy;
    }
}
