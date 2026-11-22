using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

// Frames go directly from the AirPlay socket to a loopback-only Edge WebCodecs window.
internal sealed class Viewer : IDisposable
{
    readonly TcpListener server = new TcpListener(IPAddress.Loopback,0);
    readonly string route = "/" + Guid.NewGuid().ToString("N");
    readonly ManualResetEvent ready = new ManualResetEvent(false);
    readonly Action<string> note;
    TcpClient player;
    volatile bool stopped;
    internal Viewer(Action<string> report)
    {
        note=report;
        string edge=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),@"Microsoft\Edge\Application\msedge.exe");
        if(!File.Exists(edge))throw new IOException("找不到 Microsoft Edge。");
        server.Start(4);
        try {
            int port=((IPEndPoint)server.LocalEndpoint).Port;
            var profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"iScreenX","Viewer");
            Process.Start(new ProcessStartInfo(edge,"--app=http://127.0.0.1:"+port+route+" --user-data-dir=\""+profile+"\" --no-first-run --no-default-browser-check --window-size=820,650"){UseShellExecute=false});
            ThreadPool.QueueUserWorkItem(delegate { Listen(); });
        } catch { server.Stop();throw; }
    }
    void Listen()
    {
        try {while(!stopped){var c=server.AcceptTcpClient();ThreadPool.QueueUserWorkItem(delegate{Request(c);});}}
        catch(SocketException){}catch(ObjectDisposedException){}
    }
    void Request(TcpClient c)
    {
        bool keep=false;
        try {
            c.ReceiveTimeout=5000;c.SendTimeout=5000;
            var s=c.GetStream();
            string h=Receiver.ReadHeader(s);if(h==null)return;
            string[] line=h.Split(' '); if(line.Length<3||line[0]!="GET")return;
            string path=line[1];
            if(path==route+"/video"){
                lock(this){
                    if(stopped || player!=null)return;
                    Header(s,"application/octet-stream");player=c;keep=true;ready.Set();
                }
            }else if(path==route){
                Header(s,"text/html; charset=utf-8");
                using(var page=Assembly.GetExecutingAssembly().GetManifestResourceStream("view.html"))page.CopyTo(s);
            }else if(path.StartsWith(route+"/frames/",StringComparison.Ordinal)){
                string[] parts=path.Split('/');int frames;
                if(parts.Length==5 && Int32.TryParse(parts[3],out frames) && (frames==1 || frames==120))
                    note("已显示 "+frames+" 帧 · "+parts[4]);
                Header(s,"text/plain");
            }
        }catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}
        finally{if(!keep)c.Close();}
    }
    static void Header(Stream s,string type){
        byte[] h=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: "+type+"\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");s.Write(h,0,h.Length);
    }
    internal void Send(byte type,byte[] frame,ulong stamp)
    {
        if(!ready.WaitOne(25000)||stopped||player==null)throw new IOException("显示窗口未连接");
        byte[] h=new byte[13];h[0]=type;BitConverter.GetBytes(frame.Length).CopyTo(h,1);
        BitConverter.GetBytes((stamp>>32)*1000000.0+(uint)stamp*(1000000.0/4294967296.0)).CopyTo(h,5);
        var s=player.GetStream();s.Write(h,0,h.Length);s.Write(frame,0,frame.Length);
    }
    public void Dispose(){lock(this){stopped=true;ready.Set();server.Stop();if(player!=null)player.Close();}}
}
