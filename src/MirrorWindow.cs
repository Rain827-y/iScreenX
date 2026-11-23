using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal sealed class MirrorWindow : Form
{
    readonly WebView2 browser = new WebView2();
    internal event Action<int> VolumeChanged;
    readonly Uri page;
    bool fullscreen;
    Rectangle restoredBounds;
    FormWindowState restoredState;

    internal MirrorWindow(Uri uri)
    {
        page = uri;
        Text = "iScreenX · 屏幕镜像";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(1020, area.Width), Math.Min(620, area.Height));
        MinimumSize = new Size(320, 300);
        BackColor = Color.FromArgb(35, 36, 43);
        Padding = new Padding(1);
        browser.Dock = DockStyle.Fill;
        browser.DefaultBackgroundColor = BackColor;
        Controls.Add(browser);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try {
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iScreenX", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            if (IsDisposed) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (IsDisposed) return;
            var core = browser.CoreWebView2;
            core.Settings.IsNonClientRegionSupportEnabled = true;
            core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs args) {
                args.Cancel = args.Uri != page.AbsoluteUri;
            };
            core.WebMessageReceived += delegate(object sender, CoreWebView2WebMessageReceivedEventArgs args) {
                if (args.Source != page.AbsoluteUri) return;
                string message=args.TryGetWebMessageAsString();
                int volume;
                if(message.StartsWith("volume:",StringComparison.Ordinal) && Int32.TryParse(message.Substring(7),out volume) && volume>=0 && volume<=100){
                    if(VolumeChanged!=null)VolumeChanged(volume);
                    return;
                }
                switch (message) {
                    case "minimize": WindowState = FormWindowState.Minimized; break;
                    case "close": Close(); break;
                    case "theme-light": BackColor = Color.FromArgb(248,248,250); break;
                    case "theme-dark": BackColor = Color.FromArgb(35,36,43); break;
                }
            };
            core.ContainsFullScreenElementChanged += delegate {
                if (core.ContainsFullScreenElement == fullscreen) return;
                fullscreen = core.ContainsFullScreenElement;
                SetCornerPreference();
                if (fullscreen) {
                    restoredState = WindowState;
                    restoredBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                    var screen = Screen.FromControl(this);
                    WindowState = FormWindowState.Normal;
                    Padding = Padding.Empty;
                    Bounds = screen.Bounds;
                } else {
                    Bounds = restoredBounds;
                    WindowState = restoredState;
                    Padding = new Padding(1);
                }
            };
            core.Navigate(page.AbsoluteUri);
        } catch (Exception ex) {
            if (!IsDisposed) { MessageBox.Show(this, ex.Message, "iScreenX"); Close(); }
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetCornerPreference();
    }

    void SetCornerPreference()
    {
        // Windows 11 composites the rounded edge with per-pixel antialiasing.
        // Older compositors ignore the preference and retain the system edge.
        int preference = fullscreen ? 1 : 2;
        DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        if (m.Msg == WM_NCHITTEST && !fullscreen && WindowState == FormWindowState.Normal) {
            var p = PointToClient(new Point(unchecked((int)m.LParam.ToInt64())));
            bool left = p.X < Padding.Left, right = p.X >= ClientSize.Width - Padding.Right;
            bool top = p.Y < Padding.Top, bottom = p.Y >= ClientSize.Height - Padding.Bottom;
            int hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 0;
            if (hit != 0) { m.Result = (IntPtr)hit; return; }
        }
        base.WndProc(ref m);
    }
}
