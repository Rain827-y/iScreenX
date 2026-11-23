using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

internal sealed class Viewer : IDisposable
{
    readonly TcpListener server = new TcpListener(IPAddress.Loopback, 0);
    readonly string route = "/" + Guid.NewGuid().ToString("N");
    readonly object gate = new object();
    readonly string receiverName;
    readonly MirrorWindow window;
    readonly Action<string> note;
    TcpClient player;
    string session = "", device = "";
    volatile bool stopped;

    internal Viewer(string name, Action<string> report, Action close)
    {
        receiverName = name; note = report;
        server.Start(8);
        try {
            string url = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port + route;
            window = new MirrorWindow(new Uri(url));
            window.FormClosed += delegate { if (!stopped) close(); };
            window.Show();
            ThreadPool.QueueUserWorkItem(delegate { Listen(); });
        } catch { server.Stop(); throw; }
    }
    internal string Begin(string name)
    {
        lock (gate) {
            if (stopped || session.Length != 0) throw new IOException("已有设备正在投屏");
            session = Guid.NewGuid().ToString("N");
            device = name.Replace('\r',' ').Replace('\n',' ');
            return session;
        }
    }
    internal void End(string id)
    {
        lock (gate) {
            if (session != id) return;
            session = ""; device = "";
            if (player != null) player.Close();
            player = null; Monitor.PulseAll(gate);
        }
    }
    void Listen()
    {
        try {
            while (!stopped) {
                var c = server.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(delegate { Request(c); });
            }
        } catch (SocketException) {} catch (ObjectDisposedException) {}
    }
    void Request(TcpClient c)
    {
        bool keep = false;
        try {
            c.ReceiveTimeout = c.SendTimeout = 5000;
            var s = c.GetStream();
            string h = Receiver.ReadHeader(s);
            if (h == null) return;
            string[] line = h.Split(' ');
            if (line.Length < 3) return;
            string path = line[1];
            if (line[0] != "GET") return;
            if (path == route) {
                Header(s,"text/html; charset=utf-8");
                using (var page = Assembly.GetExecutingAssembly().GetManifestResourceStream("view.html")) page.CopyTo(s);
            } else if (path == route + "/status") {
                string state;
                lock (gate) state = receiverName + "\n" + session + "\n" + device;
                Header(s,"text/plain; charset=utf-8");
                byte[] data = Encoding.UTF8.GetBytes(state); s.Write(data,0,data.Length);
            } else if (path.StartsWith(route + "/video/",StringComparison.Ordinal)) {
                lock (gate) {
                    if (stopped || session.Length == 0 || path != route + "/video/" + session || player != null) return;
                    Header(s,"application/octet-stream"); player = c; keep = true; Monitor.PulseAll(gate);
                }
            } else if (path == route + "/first-frame") {
                note("已显示首帧"); Header(s,"text/plain");
            }
        } catch (IOException) {} catch (SocketException) {} catch (ObjectDisposedException) {}
        finally { if (!keep) c.Close(); }
    }
    static void Header(Stream s,string type)
    {
        byte[] h = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: "+type+
            "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        s.Write(h,0,h.Length);
    }
    internal void Send(string id,byte type,byte[] frame,ulong stamp)
    {
        TcpClient target;
        lock (gate) {
            if (!stopped && session == id && player == null) Monitor.Wait(gate,25000);
            if (stopped || session != id || player == null) throw new IOException("显示窗口未连接");
            target = player;
        }
        byte[] h = new byte[13]; h[0] = type; BitConverter.GetBytes(frame.Length).CopyTo(h,1);
        BitConverter.GetBytes((stamp>>32)*1000000.0+(uint)stamp*(1000000.0/4294967296.0)).CopyTo(h,5);
        var s = target.GetStream(); s.Write(h,0,h.Length); s.Write(frame,0,frame.Length);
    }
    public void Dispose()
    {
        lock (gate) { stopped = true; End(session); server.Stop(); }
        if (!window.IsDisposed) window.Close();
    }
}
