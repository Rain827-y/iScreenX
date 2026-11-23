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
    internal event Action<DisplayMode> DisplayChanged;
    readonly Uri page;
    bool fullscreen;
    double aspect=16.0/9;
    int toolbarHeight=30;
    readonly Timer resizeDrag=new Timer{Interval=15};
    Point dragStart;
    Rectangle dragBounds,dragArea;
    int dragEdge;
    Rectangle restoredBounds;
    FormWindowState restoredState;
    int preferredLongEdge;
    Size lastFittedSize;
    bool pendingRestore;
    Rectangle restoredGeometry;

    internal MirrorWindow(Uri uri)
    {
        page = uri;
        Text = "iScreenX · 屏幕镜像";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(1020, area.Width), Math.Min(620, area.Height));
        MaximizeBox=false;
        BackColor = Color.FromArgb(35, 36, 43);
        Padding = new Padding(1);
        browser.Dock = DockStyle.Fill;
        browser.DefaultBackgroundColor = BackColor;
        Controls.Add(browser);
        resizeDrag.Tick+=delegate{
            if(fullscreen || (Control.MouseButtons & MouseButtons.Left)==0){resizeDrag.Stop();return;}
            Point cursor=Cursor.Position;
            bool left=dragEdge==1 || dragEdge==4 || dragEdge==7,top=dragEdge==3 || dragEdge==4 || dragEdge==5;
            double width=dragBounds.Width-Padding.Horizontal+(cursor.X-dragStart.X)*(left?-1:1);
            double height=dragBounds.Height-toolbarHeight-Padding.Vertical+(cursor.Y-dragStart.Y)*(top?-1:1);
            var room=ResizeRoom(dragArea,dragBounds,left,top);
            double requested=dragEdge<=2?width:dragEdge==3 || dragEdge==6?height*aspect:(width*aspect+height)/(aspect+1/aspect);
            var size=ProportionalSize(requested,room);
            Bounds=new Rectangle(left?dragBounds.Right-size.Width:dragBounds.Left,
                top?dragBounds.Bottom-size.Height:dragBounds.Top,size.Width,size.Height);
        };
        Disposed+=delegate{resizeDrag.Dispose();};
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
            core.Settings.IsZoomControlEnabled=false;
            core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs args) {
                args.Cancel = args.Uri != page.AbsoluteUri;
            };
            core.WebMessageReceived += delegate(object sender, CoreWebView2WebMessageReceivedEventArgs args) {
                if (args.Source != page.AbsoluteUri) return;
                string message=args.TryGetWebMessageAsString();
                if(message.StartsWith("geometry:",StringComparison.Ordinal)){
                    string[] parts=message.Substring(9).Split('/');
                    int width,height,bar,viewport;
                    if(parts.Length==4 && int.TryParse(parts[0],out width) && int.TryParse(parts[1],out height)
                        && int.TryParse(parts[2],out bar) && int.TryParse(parts[3],out viewport)
                        && width>0 && height>0 && bar>0 && viewport>0){
                        ApplyGeometry((double)width/height,(int)Math.Round((double)bar*browser.ClientSize.Width/viewport));
                    }
                    return;
                }
                if(message.StartsWith("resize:",StringComparison.Ordinal)){
                    int edge;
                    if(!fullscreen && int.TryParse(message.Substring(7),out edge) && edge>=1 && edge<=8){
                        dragStart=Cursor.Position;dragBounds=Bounds;dragArea=Screen.FromControl(this).WorkingArea;
                        preferredLongEdge=0;dragEdge=edge;resizeDrag.Start();
                    }
                    return;
                }
                if(message.StartsWith("display:",StringComparison.Ordinal)){
                    var mode=DisplayMode.Parse(message.Substring(8));
                    if(mode!=null && DisplayChanged!=null)DisplayChanged(mode);
                    return;
                }
                int volume;
                if(message.StartsWith("volume:",StringComparison.Ordinal) && Int32.TryParse(message.Substring(7),out volume) && volume>=0 && volume<=100){
                    if(VolumeChanged!=null)VolumeChanged(volume);
                    return;
                }
                switch (message) {
                    case "toggle-pin":
                        TopMost=!TopMost;
                        goto case "pin-state";
                    case "pin-state":
                        core.PostWebMessageAsString(TopMost?"pinned":"unpinned");
                        break;
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
                    FitWindow();
                }
            };
            core.Navigate(page.AbsoluteUri);
        } catch (Exception ex) {
            if (!IsDisposed) { MessageBox.Show(this, ex.Message, "iScreenX"); Close(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect {public int left,top,right,bottom;}

    // Normal windows have a one-pixel border on each side; fullscreen geometry
    // messages also use this calculation for the saved windowed bounds.
    Size ProportionalSize(double videoWidth,Rectangle area){
        const int sides=2;
        int chrome=toolbarHeight+2;
        int maximum=Math.Max(1,Math.Min(area.Width-sides,(int)Math.Floor((area.Height-chrome)*aspect)));
        int minimum=Math.Min(maximum,aspect>=1?Math.Max(320,(int)Math.Ceiling(180*aspect)):Math.Max(180,(int)Math.Ceiling(320*aspect)));
        int width=Math.Max(minimum,Math.Min(maximum,(int)Math.Round(videoWidth)));
        return new Size(width+sides,(int)Math.Round(width/aspect)+chrome);
    }
    void ApplyGeometry(double newAspect,int newToolbarHeight){
        Rectangle old=fullscreen?restoredBounds:pendingRestore?restoredGeometry:WindowState==FormWindowState.Minimized?RestoreBounds:Bounds;
        if(old.Width<=2 || old.Height<=toolbarHeight+2)return;
        if(old.Size!=lastFittedSize || preferredLongEdge==0)
            preferredLongEdge=Math.Max(old.Width-2,old.Height-toolbarHeight-2);
        aspect=newAspect;
        toolbarHeight=newToolbarHeight;
        var area=Screen.FromRectangle(old).WorkingArea;
        var size=ProportionalSize(preferredLongEdge*Math.Min(1,aspect),area);
        var centered=new Rectangle(old.Left+(old.Width-size.Width)/2,old.Top+(old.Height-size.Height)/2,size.Width,size.Height);
        var fit=FitBounds(centered,area);
        lastFittedSize=fit.Size;
        if(fullscreen)restoredBounds=fit;
        else if(WindowState==FormWindowState.Normal)Bounds=fit;
        else {pendingRestore=true;restoredGeometry=fit;}
    }
    protected override void OnResize(EventArgs e){
        base.OnResize(e);
        if(pendingRestore && WindowState==FormWindowState.Normal){
            pendingRestore=false;Bounds=restoredGeometry;
        }
    }
    static Rectangle ResizeRoom(Rectangle area,Rectangle bounds,bool left,bool top){
        return Rectangle.FromLTRB(left?area.Left:bounds.Left,top?area.Top:bounds.Top,
            left?bounds.Right:area.Right,top?bounds.Bottom:area.Bottom);
    }
    Rectangle FitBounds(Rectangle proposed,Rectangle area){
        var size=ProportionalSize(proposed.Width-2,area);
        return new Rectangle(Math.Max(area.Left,Math.Min(proposed.Left,area.Right-size.Width)),
            Math.Max(area.Top,Math.Min(proposed.Top,area.Bottom-size.Height)),size.Width,size.Height);
    }
    void FitWindow(){
        if(!fullscreen && WindowState==FormWindowState.Normal)
            Bounds=FitBounds(Bounds,Screen.FromControl(this).WorkingArea);
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
        if(m.Msg==0x216 && !fullscreen){ // WM_MOVING: the pointer can cross a seam even while the window is clamped.
            var r=(NativeRect)Marshal.PtrToStructure(m.LParam,typeof(NativeRect));
            var proposed=Rectangle.FromLTRB(r.left,r.top,r.right,r.bottom);
            var area=Screen.FromPoint(Cursor.Position).WorkingArea;
            var fit=FitBounds(proposed,area);
            r.left=fit.Left;r.top=fit.Top;r.right=fit.Right;r.bottom=fit.Bottom;
            Marshal.StructureToPtr(r,m.LParam,false);m.Result=(IntPtr)1;return;
        }
        if(m.Msg==0x214 && !fullscreen){ // WM_SIZING, preserve the opposite corner or edge.
            var r=(NativeRect)Marshal.PtrToStructure(m.LParam,typeof(NativeRect));
            int edge=m.WParam.ToInt32();
            double w=r.right-r.left-Padding.Horizontal,h=r.bottom-r.top-toolbarHeight-Padding.Vertical;
            double requested=edge<=2?w:edge==3||edge==6?h*aspect:(w*aspect+h)/(aspect+1/aspect);
            var room=ResizeRoom(Screen.FromControl(this).WorkingArea,Bounds,edge==1||edge==4||edge==7,edge==3||edge==4||edge==5);
            Size size=ProportionalSize(requested,room);
            if(edge==1||edge==4||edge==7)r.left=r.right-size.Width;else r.right=r.left+size.Width;
            if(edge==3||edge==4||edge==5)r.top=r.bottom-size.Height;else r.bottom=r.top+size.Height;
            Marshal.StructureToPtr(r,m.LParam,false);m.Result=(IntPtr)1;return;
        }
        if (m.Msg == WM_NCHITTEST && !fullscreen && WindowState == FormWindowState.Normal) {
            var p = PointToClient(new Point(unchecked((int)m.LParam.ToInt64())));
            bool left = p.X < Padding.Left, right = p.X >= ClientSize.Width - Padding.Right;
            bool top = p.Y < Padding.Top, bottom = p.Y >= ClientSize.Height - Padding.Bottom;
            int hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 0;
            if (hit != 0) { m.Result = (IntPtr)hit; return; }
        }
        base.WndProc(ref m);
        if(IsHandleCreated && !IsDisposed && (m.Msg==0x7e || m.Msg==0x1a || m.Msg==0x232)){
            // Recheck after a monitor removal, work-area change, or completion of a native drag.
            BeginInvoke((Action)delegate{
                if(IsDisposed)return;
                if(fullscreen)Bounds=Screen.FromControl(this).Bounds;else FitWindow();
            });
        }
    }
}
