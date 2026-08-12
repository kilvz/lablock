using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LabLock.Client.Services;

/// <summary>
/// Win32 helpers to force a borderless dialog onto the foreground / on top of
/// everything (including fullscreen windows), bypassing the Windows foreground lock.
/// </summary>
internal static class TopMostHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;

    /// <summary>
    /// Force the window to the foreground, working around the Windows foreground lock.
    /// </summary>
    public static void ForceToForeground(IntPtr hwnd)
    {
        // Save and disable the foreground-lock timeout so SetForegroundWindow succeeds
        uint fgLockTimeout = 0;
        SystemParametersInfo(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref fgLockTimeout, 0);
        uint zero = 0;
        SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, ref zero, 0);

        var curThread = GetCurrentThreadId();
        uint fgThread = 0;
        try { fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _); } catch { }

        if (curThread != fgThread)
            AttachThreadInput(curThread, fgThread, true);

        try
        {
            BringWindowToTop(hwnd);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (curThread != fgThread)
                AttachThreadInput(curThread, fgThread, false);

            // Restore the timeout so the OS behaves normally afterwards
            if (fgLockTimeout != 0)
                SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, ref fgLockTimeout, 0);
        }
    }

    /// <summary>
    /// Keep the window pinned on top by re-asserting HWND_TOPMOST periodically.
    /// </summary>
    public static void StartKeepOnTop(Form form)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 800 };
        timer.Tick += (_, _) =>
        {
            if (form == null || form.IsDisposed || !form.Visible) { timer.Stop(); return; }
            SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        };
        form.FormClosed += (_, _) => timer.Stop();
        timer.Start();
    }
}

/// <summary>
/// Modern borderless dialog for the "message_box" interactive tool.
/// Replaces the native MessageBox so the LabLock caption is gone and the
/// layout is clean and adaptive (no overlapping controls).
/// </summary>
public class ModernMessageDialog : Form
{
    private const int HeaderHeight = 52;
    private const int IconSize = 36;
    private const int BodyPadding = 18;
    private const int DialogWidth = 460;
    private const int CornerRadius = 12;

    private readonly Color Accent = Color.FromArgb(37, 99, 235);   // #2563EB
    private readonly Color BorderColor = Color.FromArgb(226, 232, 240);
    private readonly Color TextColor = Color.FromArgb(30, 41, 59);
    private readonly Color SubTextColor = Color.FromArgb(100, 116, 139);

    private readonly Panel _header;
    private readonly Label _titleLabel;
    private readonly PictureBox _icon;
    private readonly Label _messageLabel;
    private readonly List<Button> _buttons = new();

    public event EventHandler<string?>? ResultReady;

    public ModernMessageDialog(string title, string message, string buttons, string icon, bool topMost)
    {
        Text = string.Empty;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = topMost;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;
        KeyPreview = true;

        // Header (drag region + title + close)
        _header = new Panel
        {
            Dock = DockStyle.Top,
            Height = HeaderHeight,
            BackColor = Accent,
            Cursor = Cursors.SizeAll
        };

        _titleLabel = new Label
        {
            Text = string.IsNullOrWhiteSpace(title) ? "Notification" : title,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, (HeaderHeight - 20) / 2)
        };

        var closeBtn = new Button
        {
            Text = "\u2715",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(255, 96, 108, 158) },
            Font = new Font("Segoe UI", 10f),
            Cursor = Cursors.Hand,
            Location = new Point(DialogWidth - 40, 6),
            Size = new Size(32, 32),
            TabStop = false
        };
        closeBtn.Click += (_, _) => { ResultReady?.Invoke(this, null); Close(); };
        _header.Controls.Add(_titleLabel);
        _header.Controls.Add(closeBtn);

        // Icon
        _icon = new PictureBox
        {
            Size = new Size(IconSize, IconSize),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(BodyPadding, HeaderHeight + BodyPadding)
        };
        SetIcon(_icon, icon);

        // Message
        _messageLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(DialogWidth - BodyPadding * 2 - IconSize - 14, 0),
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 10.5f),
            Location = new Point(BodyPadding + IconSize + 14, HeaderHeight + BodyPadding + 6)
        };
        _messageLabel.Text = message;

        // Buttons (Windows 11 style, bottom-right)
        var labels = BuildButtonLabels(buttons);
        var buttonWidth = 84;
        var buttonHeight = 32;
        var gap = 8;
        int x = DialogWidth - BodyPadding - buttonWidth;
        for (int i = labels.Count - 1; i >= 0; i--)
        {
            var btn = new Button
            {
                Text = labels[i],
                Tag = labels[i],
                Width = buttonWidth,
                Height = buttonHeight,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9.5f),
                BackColor = i == labels.Count - 1 ? Accent : Color.White,
                ForeColor = i == labels.Count - 1 ? Color.White : SubTextColor,
                FlatAppearance = { BorderSize = 1, BorderColor = BorderColor }
            };
            if (i == labels.Count - 1)
                btn.FlatAppearance.BorderSize = 0;
            btn.Click += (sender, _) =>
            {
                var tag = ((Button)sender!).Tag?.ToString();
                ResultReady?.Invoke(this, tag);
                Close();
            };
            btn.Location = new Point(x, 0); // set Y after measuring message
            _buttons.Insert(0, btn);
            x -= buttonWidth + gap;
        }

        Controls.Add(_messageLabel);
        Controls.Add(_icon);
        Controls.Add(_header);

        Load += (_, _) =>
        {
            LayoutBody();
            TopMost = true;
            Activate();
            BringToFront();
            TopMostHelper.ForceToForeground(Handle);
            TopMostHelper.StartKeepOnTop(this);
        };
    }

    private void LayoutBody()
    {
        // Measure message height after it has been assigned
        var msgSize = TextRenderer.MeasureText(_messageLabel.Text, _messageLabel.Font,
            _messageLabel.MaximumSize, TextFormatFlags.WordBreak);
        _messageLabel.AutoSize = false;
        _messageLabel.Size = new Size(_messageLabel.MaximumSize.Width, Math.Max(msgSize.Height, 24));

        var iconTop = HeaderHeight + BodyPadding;
        var textTop = HeaderHeight + BodyPadding + 4;
        _messageLabel.Location = new Point(BodyPadding + IconSize + 14, textTop);
        _icon.Location = new Point(BodyPadding, iconTop);

        var contentBottom = Math.Max(_messageLabel.Bottom, _icon.Bottom);
        var btnTop = contentBottom + 20;

        // Buttons bottom-right
        const int btnW = 84;
        const int btnH = 32;
        const int gap = 8;
        int x = DialogWidth - BodyPadding - btnW;
        for (int i = _buttons.Count - 1; i >= 0; i--)
        {
            _buttons[i].Location = new Point(x, btnTop);
            _buttons[i].Size = new Size(btnW, btnH);
            x -= btnW + gap;
        }

        ClientSize = new Size(DialogWidth, btnTop + btnH + BodyPadding);
        ApplyRoundedCorners();

        // Make header draggable (only the panel background and title, NOT the close button)
        _header.MouseDown += Header_MouseDown;
        _titleLabel.MouseDown += Header_MouseDown;
    }

    private void Header_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, IntPtr.Zero);
        }
    }

    private void ApplyRoundedCorners()
    {
        // Round only the BOTTOM corners; keep the top straight so the accent
        // header fills the full width cleanly (no white slivers at the top).
        using var path = new GraphicsPath();
        var r = CornerRadius;
        int w = Width, h = Height;
        path.AddLine(0, 0, w, 0);
        path.AddArc(w - r, h - r, r, r, 0, 90);
        path.AddArc(0, h - r, r, r, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    private static void SetIcon(PictureBox pic, string icon)
    {
        Icon? sysIcon = icon.ToLowerInvariant() switch
        {
            var s when s.Contains("warn") || s.Contains("exclam") => SystemIcons.Warning,
            var s when s.Contains("error") || s.Contains("stop") || s.Contains("hand") => SystemIcons.Error,
            var s when s.Contains("question") => SystemIcons.Question,
            _ => SystemIcons.Information
        };
        pic.Image = new Bitmap(sysIcon.ToBitmap(), new Size(IconSize, IconSize));
    }

    private static List<string> BuildButtonLabels(string buttons)
    {
        var map = new Dictionary<string, string[]>
        {
            ["OK"] = new[] { "OK" },
            ["OKCancel"] = new[] { "OK", "Cancel" },
            ["YesNo"] = new[] { "Yes", "No" },
            ["YesNoCancel"] = new[] { "Yes", "No", "Cancel" },
            ["AbortRetryIgnore"] = new[] { "Abort", "Retry", "Ignore" },
            ["RetryCancel"] = new[] { "Retry", "Cancel" }
        };
        if (map.TryGetValue(buttons, out var labels))
            return new List<string>(labels);
        return new List<string> { "OK" };
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HT_CAPTION = 0x2;
}

/// <summary>
/// Modern input dialog for the "interactive_message" tool. Uses explicit
/// post-measurement layout so labels never overlap the input box.
/// </summary>
public class ModernInputDialog : Form
{
    private const int HeaderHeight = 52;
    private const int BodyPadding = 18;
    private const int DialogWidth = 480;
    private const int CornerRadius = 12;

    private readonly Color Accent = Color.FromArgb(37, 99, 235);
    private readonly Color BorderColor = Color.FromArgb(226, 232, 240);
    private readonly Color TextColor = Color.FromArgb(30, 41, 59);
    private readonly Color SubTextColor = Color.FromArgb(100, 116, 139);

    private readonly Panel _header;
    private readonly Label _titleLabel;
    private readonly Label _messageLabel;
    private readonly TextBox _input;
    private readonly Button _okBtn;
    private readonly Button _cancelBtn;
    private readonly bool _allowEmpty;

    public event EventHandler<string?>? ResultReady;

    public ModernInputDialog(string title, string message, string placeholder, bool allowEmpty, bool topMost)
    {
        _allowEmpty = allowEmpty;
        Text = string.Empty;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = topMost;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;
        KeyPreview = true;

        _header = new Panel
        {
            Dock = DockStyle.Top,
            Height = HeaderHeight,
            BackColor = Accent,
            Cursor = Cursors.SizeAll
        };

        _titleLabel = new Label
        {
            Text = string.IsNullOrWhiteSpace(title) ? "Notification" : title,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, (HeaderHeight - 20) / 2)
        };

        var closeBtn = new Button
        {
            Text = "\u2715",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(255, 96, 108, 158) },
            Font = new Font("Segoe UI", 10f),
            Cursor = Cursors.Hand,
            Location = new Point(DialogWidth - 40, 6),
            Size = new Size(32, 32),
            TabStop = false
        };
        closeBtn.Click += (_, _) => { ResultReady?.Invoke(this, null); Close(); };
        _header.Controls.Add(_titleLabel);
        _header.Controls.Add(closeBtn);

        _messageLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(DialogWidth - BodyPadding * 2, 0),
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 10.5f),
            Location = new Point(BodyPadding, HeaderHeight + BodyPadding)
        };
        _messageLabel.Text = message;

        _input = new TextBox
        {
            Font = new Font("Segoe UI", 10f),
            PlaceholderText = placeholder,
            BackColor = Color.FromArgb(248, 250, 252),
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle
        };

        _okBtn = new Button
        {
            Text = "Send",
            Width = 84,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9.5f),
            FlatAppearance = { BorderSize = 0 }
        };

        _cancelBtn = new Button
        {
            Text = "Cancel",
            Width = 84,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = SubTextColor,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9.5f),
            FlatAppearance = { BorderSize = 1, BorderColor = BorderColor }
        };

        _okBtn.Click += (_, _) => ResultReady?.Invoke(this, _input.Text);
        _cancelBtn.Click += (_, _) => ResultReady?.Invoke(this, null);
        _input.TextChanged += (_, _) => _okBtn.Enabled = _allowEmpty || _input.Text.Length > 0;
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (_allowEmpty || _input.Text.Length > 0)
                    ResultReady?.Invoke(this, _input.Text);
            }
        };

        Controls.Add(_messageLabel);
        Controls.Add(_input);
        Controls.Add(_cancelBtn);
        Controls.Add(_okBtn);
        Controls.Add(_header);

        AcceptButton = _okBtn;
        CancelButton = _cancelBtn;
        Load += (_, _) =>
        {
            LayoutBody();
            TopMost = true;
            Activate();
            _input.Focus();
            BringToFront();
            TopMostHelper.ForceToForeground(Handle);
            TopMostHelper.StartKeepOnTop(this);
        };
    }

    private void LayoutBody()
    {
        var msgSize = TextRenderer.MeasureText(_messageLabel.Text, _messageLabel.Font,
            _messageLabel.MaximumSize, TextFormatFlags.WordBreak);
        _messageLabel.AutoSize = false;
        _messageLabel.Size = new Size(_messageLabel.MaximumSize.Width, Math.Max(msgSize.Height, 22));
        _messageLabel.Location = new Point(BodyPadding, HeaderHeight + BodyPadding);

        var inputTop = _messageLabel.Bottom + 14;
        _input.Location = new Point(BodyPadding, inputTop);
        _input.Width = DialogWidth - BodyPadding * 2;
        _input.Height = 36;

        var btnTop = _input.Bottom + 18;
        _cancelBtn.Location = new Point(DialogWidth - BodyPadding - 84, btnTop);
        _okBtn.Location = new Point(DialogWidth - BodyPadding - 84 - 84 - 8, btnTop);

        ClientSize = new Size(DialogWidth, btnTop + 32 + BodyPadding);
        ApplyRoundedCorners();

        _header.MouseDown += Header_MouseDown;
        _titleLabel.MouseDown += Header_MouseDown;
    }

    private void Header_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, IntPtr.Zero);
        }
    }

    private void ApplyRoundedCorners()
    {
        // Round only the BOTTOM corners so the accent header stays full-width.
        using var path = new GraphicsPath();
        var r = CornerRadius;
        int w = Width, h = Height;
        path.AddLine(0, 0, w, 0);
        path.AddArc(w - r, h - r, r, r, 0, 90);
        path.AddArc(0, h - r, r, r, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HT_CAPTION = 0x2;
}
