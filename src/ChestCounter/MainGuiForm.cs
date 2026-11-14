using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ChestCounter;

/// <summary>
/// Modern native desktop window hosting 1:2 layout WebView2 dashboard,
/// with System Tray minimization and quick-action menu.
/// </summary>
public class MainGuiForm : Form
{
    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    private static extern int SetPreferredAppMode(int preferredAppMode);

    [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = true)]
    private static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);

    [DllImport("uxtheme.dll", CharSet = CharSet.Auto)]
    private static extern int SetWindowTheme(IntPtr hWnd, string textSubAppName, string? textSubIdList);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    public static void InitializeTheme()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SetPreferredAppMode(2); // 2 = ForceDark
            }
        }
        catch { }
    }

    private static void ApplyDarkTitleBar(IntPtr hWnd)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                try { SetPreferredAppMode(2); } catch { }
                try { AllowDarkModeForWindow(hWnd, true); } catch { }
                try { SetWindowTheme(hWnd, "DarkMode_Explorer", null); } catch { }

                int darkMode = 1;
                // Attribute 20 (Windows 10 2004+ and Windows 11)
                if (DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int)) != 0)
                {
                    // Fallback to Attribute 19 (Windows 10 1809 - 1909)
                    DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
                }

                // Custom Caption Color for Windows 11 (#0d0f14 -> BGR 0x00140F0D)
                int captionColor = 0x00140F0D;
                DwmSetWindowAttribute(hWnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

                // Text Color (White)
                int textColor = 0x00FFFFFF;
                DwmSetWindowAttribute(hWnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));

                // Border Color (#1f242d -> BGR 0x002D241F)
                int borderColor = 0x002D241F;
                DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            }
        }
        catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDarkTitleBar(this.Handle);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyDarkTitleBar(this.Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyDarkTitleBar(this.Handle);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        ApplyDarkTitleBar(this.Handle);
    }

    private const int WM_NCACTIVATE = 0x0086;
    private const int WM_ACTIVATEAPP = 0x001C;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_NCACTIVATE || m.Msg == WM_ACTIVATEAPP)
        {
            ApplyDarkTitleBar(this.Handle);
        }
    }

    private readonly WebView2 _webView;
    private readonly string _dashboardUrl;
    private readonly Action? _onExit;
    private readonly NotifyIcon _trayIcon;
    private bool _allowExit = false;
    private bool _hasShownTrayNotice = false;
    private bool _browserFallbackOpened;

    public static MainGuiForm? CurrentInstance { get; private set; }

    public MainGuiForm(string dashboardUrl, Action? onExit = null, bool startMinimized = false)
    {
        CurrentInstance = this;
        _dashboardUrl = dashboardUrl;
        _onExit = onExit;

        Text = "Burnaviour Chest Tracker";
        Size = new Size(540, 720);
        MinimumSize = new Size(460, 560);
        MaximumSize = Size.Empty;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(13, 15, 20);

        if (startMinimized)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            _hasShownTrayNotice = true;
        }

        // Load Icon
        try
        {
            var iconCandidatePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "src", "ChestCounter", "app.ico"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico"),
                Path.Combine(Directory.GetCurrentDirectory(), "src", "ChestCounter", "app.ico"),
                Path.Combine(Directory.GetCurrentDirectory(), "v3", "public", "assets", "app.ico"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "public", "assets", "chest_fnv1.png")
            };

            foreach (var p in iconCandidatePaths)
            {
                if (File.Exists(p))
                {
                    if (p.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        Icon = new Icon(p);
                    }
                    else
                    {
                        using var bmp = new Bitmap(p);
                        Icon = Icon.FromHandle(bmp.GetHicon());
                    }
                    break;
                }
            }
        }
        catch { /* Fallback to default */ }

        // Setup System Tray Icon & Context Menu
        _trayIcon = new NotifyIcon
        {
            Icon = this.Icon ?? SystemIcons.Application,
            Text = "Burnaviour Chest Tracker",
            Visible = true
        };

        var trayMenu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem(DashboardLanguage.Text("openApp"), null, (_, _) => RestoreWindow())
        {
            Font = new Font(Font, FontStyle.Bold)
        };
        var openWebItem = new ToolStripMenuItem(DashboardLanguage.Text("openWeb"), null, (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(WebDashboardServer.Url + "dashboard") { UseShellExecute = true });
            }
            catch { }
        });
        var copyOverlayItem = new ToolStripMenuItem(DashboardLanguage.Text("copy"), null, (_, _) =>
        {
            try
            {
                Clipboard.SetText(WebDashboardServer.OverlayUrl);
                _trayIcon.ShowBalloonTip(1500, "Burnaviour Chest Tracker", DashboardLanguage.Text("copied"), ToolTipIcon.Info);
            }
            catch { }
        });
        var openOverlayItem = new ToolStripMenuItem(DashboardLanguage.Text("preview"), null, (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(WebDashboardServer.OverlayUrl) { UseShellExecute = true });
            }
            catch { }
        });
        var exitItem = new ToolStripMenuItem(DashboardLanguage.Text("exit"), null, (_, _) =>
        {
            _allowExit = true;
            Close();
            Application.Exit();
        });

        trayMenu.Items.Add(openItem);
        trayMenu.Items.Add(openWebItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(copyOverlayItem);
        trayMenu.Items.Add(openOverlayItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = trayMenu;
        _trayIcon.DoubleClick += (_, _) => RestoreWindow();

        // Setup WebView2 Control
        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(13, 15, 20)
        };

        Controls.Add(_webView);
        InitWebView();
    }

    private void RestoreWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    public void RefreshLanguage()
    {
        if (InvokeRequired) { BeginInvoke(RefreshLanguage); return; }
        if (_trayIcon.ContextMenuStrip is not { } menu) return;
        var keys = new[] { "openApp", "openWeb", "", "copy", "preview", "", "exit" };
        for (var i = 0; i < keys.Length; i++) if (keys[i].Length > 0) menu.Items[i].Text = DashboardLanguage.Text(keys[i]);
        menu.RightToLeft = DashboardLanguage.Code == "ar" ? RightToLeft.Yes : RightToLeft.No;
    }

    private async void InitWebView()
    {
        try
        {
            var userDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BurnaviourChestTracker", "WebView2");
            Directory.CreateDirectory(userDir);
            var env = await CoreWebView2Environment.CreateAsync(null, userDir);
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            SetZoomPercent(WebDashboardServer.UiZoomPercent);
            _webView.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                try
                {
                    Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
                }
                catch { }
            };
            _webView.Source = new Uri(_dashboardUrl);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Native UI startup", ex);
            ShowRuntimeFallback();
        }
    }

    private void ShowRuntimeFallback()
    {
        Controls.Clear();
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24), ColumnCount = 1, RowCount = 5 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var heading = new Label { Text = AppInfo.AppName, AutoSize = true, ForeColor = Color.FromArgb(245,166,35), Font = new Font("Segoe UI", 20, FontStyle.Bold), Margin = new Padding(0,0,0,18) };
        var message = new Label { Text = DashboardLanguage.Text("runtimeHelp"), AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 11), MaximumSize = new Size(Math.Max(180, ClientSize.Width-80), 0), Margin = new Padding(0,0,0,18) };
        var address = new TextBox { Text = _dashboardUrl, ReadOnly = true, Dock = DockStyle.Top, BackColor = Color.FromArgb(20,24,32), ForeColor = Color.White, Margin = new Padding(0,0,0,18) };
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, WrapContents = true };
        Button ActionButton(string label, Action action)
        {
            var button = new Button { Text = label, AutoSize = true, Padding = new Padding(12,8,12,8), BackColor = Color.FromArgb(35,42,54), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(0,0,10,10) };
            button.Click += (_,_) => action(); actions.Controls.Add(button); return button;
        }
        void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
        ActionButton(DashboardLanguage.Text("openWeb"), () => Open(_dashboardUrl));
        ActionButton(DashboardLanguage.Text("installWebView"), () => Open("https://developer.microsoft.com/microsoft-edge/webview2/"));
        ActionButton(DashboardLanguage.Text("retry"), () => { Controls.Clear(); Controls.Add(_webView); InitWebView(); });
        panel.Controls.Add(heading); panel.Controls.Add(message); panel.Controls.Add(address); panel.Controls.Add(actions);
        panel.Resize += (_,_) => message.MaximumSize = new Size(Math.Max(180,panel.ClientSize.Width-20),0);
        Controls.Add(panel);
        if (!_browserFallbackOpened) { _browserFallbackOpened = true; Open(_dashboardUrl); }
    }

    public void SetZoomPercent(int percent)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => SetZoomPercent(percent))); return; }
        _webView.ZoomFactor = Math.Clamp(percent, 50, 150) / 100.0;
    }

    /// <summary>Closes the window for good (quitting, or restarting after an update), instead of hiding it in the tray.</summary>
    public void ExitApp()
    {
        _allowExit = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BurnaviourChestTracker", "trace.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [MainGuiForm] OnFormClosing with reason: {e.CloseReason}, allowExit={_allowExit}\r\n");
        }
        catch { }

        if (!_allowExit && e.CloseReason != CloseReason.WindowsShutDown)
        {
            e.Cancel = true;
            Hide();
            if (!_hasShownTrayNotice)
            {
                _hasShownTrayNotice = true;
                _trayIcon.ShowBalloonTip(2500, "Burnaviour Chest Tracker", DashboardLanguage.Text("openApp"), ToolTipIcon.Info);
            }
            return;
        }

        try
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        catch { }

        base.OnFormClosing(e);
        _onExit?.Invoke();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (ReferenceEquals(CurrentInstance, this))
        {
            CurrentInstance = null;
        }
    }
}
