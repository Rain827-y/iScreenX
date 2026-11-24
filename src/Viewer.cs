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
    byte[] videoConfig;
    bool needsConfig, connectedOnce;
    string session = "", device = "";
    IPAddress peer;
    volatile bool stopped;
    internal volatile int AudioVolume=75;
    internal volatile DisplayMode DesiredDisplay=DisplayMode.Load();
    volatile int audioDelay=75, lastAudioTick;
    internal void AudioUpdated(int delay){
        audioDelay=delay;
        lastAudioTick=Environment.TickCount;
    }

    internal Viewer(string name, Action<string> report, Action close,Func<bool,bool> controlMode)
    {
        receiverName = name; note = report;
        server.Start(8);
        try {
            string url = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port + route;
            window = new MirrorWindow(new Uri(url));
            window.ControlMode=controlMode;
            window.CheckSsh=CheckSsh;
            window.VolumeChanged += delegate(int value){AudioVolume=value;};
            window.DisplayChanged += delegate(DisplayMode mode){DesiredDisplay=mode;mode.Save();};
            window.FormClosed += delegate { if (!stopped) close(); };
            window.Show();
            ThreadPool.QueueUserWorkItem(delegate { Listen(); });
        } catch { server.Stop(); throw; }
    }
    string CheckSsh(int port,string password)
    {
        string id;
        IPAddress target;
        lock (gate) { id=session; target=peer; }
        string result=SshConnectionCheck.Run(target,port,password);
        lock (gate) return session==id?result:"投屏设备已断开或切换，请重新检测。";
    }
    internal string Begin(string name,IPAddress address)
    {
        lock (gate) {
            if (stopped || session.Length != 0) throw new IOException("已有设备正在投屏");
            session = Guid.NewGuid().ToString("N");
            peer=address;
            videoConfig=null; connectedOnce=false;
            device = name.Replace('\r',' ').Replace('\n',' ');
            return session;
        }
    }
    internal void End(string id)
    {
        lock (gate) {
            if (session != id) return;
            session = ""; device = ""; peer=null; audioDelay=75;
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
            c.NoDelay = true;
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
                using(var page=new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("view.html"),Encoding.UTF8)){
                    byte[] html=Encoding.UTF8.GetBytes(page.ReadToEnd().Replace("{{displayMode}}",DesiredDisplay.ToString()));
                    s.Write(html,0,html.Length);
                }
            } else if (path == route + "/status") {
                string state;
                lock (gate) state = receiverName + "\n" + session + "\n" + device;
                Header(s,"text/plain; charset=utf-8");
                byte[] data = Encoding.UTF8.GetBytes(state); s.Write(data,0,data.Length);
            } else if (path.StartsWith(route + "/video/",StringComparison.Ordinal)) {
                lock (gate) {
                    if (stopped || session.Length == 0 || path != route + "/video/" + session) return;
                    Header(s,"application/octet-stream");
                    if(player!=null)player.Close();
                    player=c;needsConfig=true;keep=true;Monitor.PulseAll(gate);
                    if(connectedOnce)note("本地显示通道已重新连接");
                    connectedOnce=true;
                }
            } else if (path.StartsWith(route+"/vstats/",StringComparison.Ordinal)) {
                string[] parts=path.Split('/');int decoded,displayed,queue,median,p95,source;
                if(parts.Length==9 && int.TryParse(parts[3],out decoded) && int.TryParse(parts[4],out displayed) && int.TryParse(parts[5],out queue) && int.TryParse(parts[6],out median) && int.TryParse(parts[7],out p95) && int.TryParse(parts[8],out source))
                    note("显示统计：解码 "+decoded+"，显示 "+displayed+"，最大解码队列 "+queue+"，收包到绘制 P50/P95 "+median+"/"+p95+"ms，发送端时间戳到收包 P50 "+(source<0?"未知":source+"ms"));
                Header(s,"text/plain");
            } else if (path == route + "/delay") {
                Header(s,"text/plain");
                int delay=unchecked((uint)(Environment.TickCount-lastAudioTick))<650?audioDelay:75;
                byte[] text=Encoding.ASCII.GetBytes(delay.ToString(System.Globalization.CultureInfo.InvariantCulture));
                s.Write(text,0,text.Length);
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
    internal void Send(string id,byte type,byte[] frame,ulong stamp,double arrival,double source)
    {
        TcpClient target;
        byte[] replay=null;
        lock (gate) {
            if(session!=id || stopped)return;
            if(type==1)videoConfig=frame;
            if(!connectedOnce && player==null && type==1)Monitor.Wait(gate,800);
            if(session!=id || stopped || player==null)return;
            target = player;
            if(needsConfig && type!=1)replay=videoConfig;
            needsConfig=false;
        }
        try {
            var stream=target.GetStream();
            if(replay!=null)WriteFrame(stream,1,replay,stamp,arrival,source);
            WriteFrame(stream,type,frame,stamp,arrival,source);
        } catch(IOException e){PlayerDisconnected(target,e);}
        catch(InvalidOperationException e){PlayerDisconnected(target,e);}
    }
    void PlayerDisconnected(TcpClient target,Exception error){
        lock(gate){
            if(player!=target)return;
            player=null;target.Close();
            note("本地显示连接中断，继续接收手机视频 · "+error.Message);
        }
    }
    static void WriteFrame(Stream s,byte type,byte[] frame,ulong stamp,double arrival,double source){
        byte[] h = new byte[29]; h[0] = type; BitConverter.GetBytes(frame.Length).CopyTo(h,1);
        BitConverter.GetBytes((stamp>>32)*1000000.0+(uint)stamp*(1000000.0/4294967296.0)).CopyTo(h,5);
        BitConverter.GetBytes(arrival).CopyTo(h,13);BitConverter.GetBytes(source).CopyTo(h,21);
        s.Write(h,0,h.Length); s.Write(frame,0,frame.Length);
    }
    public void Dispose()
    {
        lock (gate) { stopped = true; End(session); server.Stop(); }
        if (!window.IsDisposed) window.Close();
    }
}
