namespace LabLock.Installer;

public sealed class InstallerForm : Form
{
    private readonly string _installDir;
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;
    private readonly Button _installButton;
    private readonly Button _closeButton;

    public InstallerForm(string? overrideDir = null)
    {
        _installDir = overrideDir ?? InstallerCore.DefaultInstallDir;

        Text = "LabLock Server Installer";
        ClientSize = new Size(460, 224);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var title = new Label
        {
            Text = "LabLock Server",
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Color.FromArgb(10, 132, 255),
            AutoSize = true,
            Location = new Point(24, 16)
        };

        var subtitle = new Label
        {
            Text = $"Installs the LabLock server + MCP agent to:\n{_installDir}\nAdds a shortcut to your desktop.",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(90, 90, 100),
            AutoSize = true,
            Location = new Point(26, 54)
        };

        _statusLabel = new Label
        {
            Text = InstallerCore.IsInstalled(_installDir)
                ? "LabLock is already installed. Click Reinstall to update."
                : "Ready to install.",
            Font = new Font("Segoe UI", 9),
            AutoSize = true,
            Location = new Point(26, 110)
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(26, 138),
            Size = new Size(408, 16),
            Minimum = 0,
            Maximum = 100
        };

        _installButton = new Button
        {
            Text = InstallerCore.IsInstalled(_installDir) ? "Reinstall" : "Install",
            Size = new Size(110, 32),
            Location = new Point(203, 180)
        };
        _installButton.Click += async (_, _) => await InstallAsync();

        _closeButton = new Button
        {
            Text = "Close",
            Size = new Size(110, 32),
            Location = new Point(324, 180),
            Enabled = false
        };
        _closeButton.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { title, subtitle, _statusLabel, _progressBar, _installButton, _closeButton });
    }

    private async Task InstallAsync()
    {
        _installButton.Enabled = false;
        _statusLabel.Text = "Installing...";
        _progressBar.Value = 5;

        try
        {
            await Task.Run(() => InstallerCore.Install(
                _installDir,
                p => BeginInvoke(() => _progressBar.Value = p),
                m => BeginInvoke(() => _statusLabel.Text = m)));

            _statusLabel.Text = $"Installed to {_installDir}";
            _installButton.Text = "Reinstall";
            _closeButton.Enabled = true;

            MessageBox.Show(
                $"LabLock Server installed successfully to:\n{_installDir}\n\n" +
                "A shortcut has been added to your desktop.\n\n" +
                "Run the installed app to start the server. It also serves as the\n" +
                "MCP agent for opencode (auto-detects MCP mode when stdin is piped).",
                "LabLock Installer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Install failed.";
            MessageBox.Show($"Install failed:\n{ex.Message}", "LabLock Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _installButton.Enabled = true;
            _closeButton.Enabled = true;
        }
    }
}
