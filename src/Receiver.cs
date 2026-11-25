using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class Receiver : Form
{
    Viewer viewer;
    readonly Label status = new Label { Dock = DockStyle.Top, Height = 60, Padding = new Padding(12) };
    readonly ListBox log = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    readonly List<IntPtr> registrations = new List<IntPtr>();
    readonly Semaphore clients = new Semaphore(8, 8);
    TcpListener listener, ipv6Listener;
    IPAddress ipv6Address;
    uint lanIndex;
    bool ipv4Only;
    string device, serviceName;
    byte[] identity, publicKey;
    const string Features = "0x5A7FFFF7";
    const string Model = "AppleTV3,2";
    const string Version = "220.68";
    volatile bool closing;

    [DllImport("dnssd.dll", CharSet = CharSet.Ansi)]
    static extern int DNSServiceRegister(out IntPtr service, uint flags, uint iface, string name, string type, string domain, string host, ushort port, ushort length, byte[] txt, IntPtr callback, IntPtr context);
    [DllImport("dnssd.dll")]
    static extern void DNSServiceRefDeallocate(IntPtr service);
    [DllImport("dnssd.dll")]
    static extern int DNSServiceCreateConnection(out IntPtr service);
    delegate void RecordReply(IntPtr service,IntPtr record,uint flags,int error,IntPtr context);
    static readonly RecordReply AddressReply=delegate{};
    [DllImport("dnssd.dll",CharSet=CharSet.Ansi)]
    static extern int DNSServiceRegisterRecord(IntPtr service,out IntPtr record,uint flags,uint index,string name,
        ushort type,ushort rrclass,ushort length,byte[] data,uint ttl,RecordReply reply,IntPtr context);

    void Register(uint index, string host,string name, string type, int port, params string[] values)
    {
        MemoryStream txt = new MemoryStream();
        foreach (string value in values) {
            byte[] data = Encoding.ASCII.GetBytes(value);
            txt.WriteByte((byte)data.Length);
            txt.Write(data, 0, data.Length);
        }
        IntPtr reference;
        int err = DNSServiceRegister(out reference, 0, index, name, type, "local.", host,
            (ushort)IPAddress.HostToNetworkOrder((short)port), (ushort)txt.Length, txt.ToArray(), IntPtr.Zero, IntPtr.Zero);
        if (err != 0) throw new IOException("Bonjour 注册失败 (" + err + ")");
        registrations.Add(reference);
    }
    void Start()
    {
        identity = PairKeys.LoadIdentity();
        publicKey = PairKeys.Public(PairKeys.Ed, identity);
        // Identify this receiver independently of other AirPlay apps on the same adapter.
        byte[] receiverId = new byte[6];
        using (var sha = System.Security.Cryptography.SHA256.Create())
            Array.Copy(sha.ComputeHash(publicKey), receiverId, receiverId.Length);
        receiverId[0] = (byte)((receiverId[0] & 0xfc) | 0x02);
        device = BitConverter.ToString(receiverId).Replace("-", ":");
        serviceName = "iScreenX-" + Environment.MachineName;
        UpdateNetwork();
        viewer = new Viewer(serviceName, Note, delegate { if (!closing) BeginInvoke((Action)Close); },SetControlMode);
        networkRefresh.Tick+=delegate {
            try { UpdateNetwork(); }
            catch(Exception ex){ if(lastNetworkError!=ex.Message)Note(ex.Message); lastNetworkError=ex.Message; }
        };
        networkRefresh.Start();
    }
    readonly System.Windows.Forms.Timer networkRefresh=new System.Windows.Forms.Timer{Interval=4000};
    string lastNetworkError;
    static NetworkInterface SelectNetwork(out IPAddress address){
        NetworkInterface chosen = null;
        address = null;
        int best = -1;
        foreach (NetworkInterface net in NetworkInterface.GetAllNetworkInterfaces()) {
            if (net.OperationalStatus != OperationalStatus.Up || !net.SupportsMulticast ||
                net.GetPhysicalAddress().GetAddressBytes().Length != 6) continue;
            var properties = net.GetIPProperties();
            int score = net.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 2 :
                        net.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 1 : 0;
            foreach (var gateway in properties.GatewayAddresses) {
                if (gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)) {
                    score += 4; break;
                }
            }
            if (score <= best) continue;
            foreach (var ip in properties.UnicastAddresses) {
                if (ip.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip.Address)) continue;
                chosen = net; address = ip.Address; best = score; break;
            }
        }
        if (chosen == null)
            throw new IOException("未找到可用的局域网网卡。");
        return chosen;
    }
    void UpdateNetwork(){
        IPAddress address;
        var chosen=SelectNetwork(out address);
        uint index=(uint)chosen.GetIPProperties().GetIPv4Properties().Index;
        IPAddress nextV6=null;
        foreach(var ip in chosen.GetIPProperties().UnicastAddresses){
            if(ip.Address.IsIPv6LinkLocal){nextV6=ip.Address;break;}
        }
        bool sameV4=listener!=null && address.Equals(((IPEndPoint)listener.LocalEndpoint).Address);
        if(sameV4 && index==lanIndex && Object.Equals(nextV6,ipv6Address) && registrations.Count>0 && lastNetworkError==null)return;
        TcpListener next=listener;
        if(!sameV4){
            next=new TcpListener(address,0);
            next.Start(8);
        }
        ClearAdvertisement();
        if(ipv6Listener!=null){ipv6Listener.Stop();ipv6Listener=null;}
        if(!sameV4 && listener!=null)listener.Stop();
        listener=next;lanIndex=index;ipv6Address=nextV6;
        if(!sameV4)ThreadPool.QueueUserWorkItem(delegate{Accept(next);});
        if(!ipv4Only)StartIPv6();
        Advertise(ipv4Only);
        lastNetworkError=null;
    }
    void StartIPv6(){
        if(ipv6Address==null || ipv6Listener!=null)return;
        var next=new TcpListener(ipv6Address,((IPEndPoint)listener.LocalEndpoint).Port);
        next.Start(8);ipv6Listener=next;
        ThreadPool.QueueUserWorkItem(delegate{Accept(next);});
    }
    void ClearAdvertisement(){
        for(int i=registrations.Count-1;i>=0;i--)DNSServiceRefDeallocate(registrations[i]);
        registrations.Clear();
    }
    void Advertise(bool onlyV4){
        var endpoint=(IPEndPoint)listener.LocalEndpoint;
        int port=endpoint.Port;
        string pk="pk="+BitConverter.ToString(publicKey).Replace("-","").ToLowerInvariant();
        string host=null;
        uint index=lanIndex;
        if(onlyV4){
            host="iscreenx-"+device.Replace(":","").ToLowerInvariant()+"-v4.local.";
            IntPtr service,record;
            int err=DNSServiceCreateConnection(out service);
            if(err!=0)throw new IOException("Bonjour IPv4 连接失败 ("+err+")");
            registrations.Add(service);
            err=DNSServiceRegisterRecord(service,out record,0x20,index,host,1,1,4,endpoint.Address.GetAddressBytes(),120,AddressReply,IntPtr.Zero);
            if(err!=0)throw new IOException("Bonjour IPv4 地址注册失败 ("+err+")");
        }
        Register(index,host, serviceName, "_airplay._tcp", port,
            "deviceid=" + device, "features=" + Features, "flags=0x4", "model=" + Model, "srcvers=" + Version, "vv=2", pk);
        Register(index,host, device.Replace(":", "") + "@" + serviceName, "_raop._tcp", port,
            "txtvers=1", "ch=2", "cn=0,1,2,3", "et=0,3,5", "md=0,1,2", "pw=false", "sr=44100", "ss=16",
            "tp=UDP", "vn=65537", "vs=" + Version, "am=" + Model, "ft=" + Features, "sf=0x4", "vv=2", pk);
        status.Text=serviceName+"    "+endpoint;
    }
    bool SetControlMode(bool enable){
        if(enable==ipv4Only)return ipv4Only;
        ClearAdvertisement();
        try{
            if(!enable)StartIPv6();
            Advertise(enable);
            if(enable && ipv6Listener!=null){ipv6Listener.Stop();ipv6Listener=null;}
            ipv4Only=enable;
            Note(enable?"新连接仅监听 IPv4":"已恢复普通监听模式");
        }catch(Exception ex){
            Note("监听模式切换失败："+ex.Message);
            if(ipv4Only && ipv6Listener!=null){ipv6Listener.Stop();ipv6Listener=null;}
            ClearAdvertisement();Advertise(ipv4Only);
        }
        return ipv4Only;
    }
    void Shutdown()
    {
        closing = true;
        networkRefresh.Dispose();
        if (viewer != null) viewer.Dispose();
        if (listener != null) listener.Stop();
        if (ipv6Listener != null) ipv6Listener.Stop();
        ClearAdvertisement();
    }
    void Note(string s)
    {
        if (closing) return;
        try { BeginInvoke((Action)delegate {
            if (log.Items.Count == 150) log.Items.RemoveAt(0);
            log.Items.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + s);
            log.TopIndex = log.Items.Count - 1;
        }); } catch (InvalidOperationException) {}
    }
    void Accept(TcpListener source)
    {
        try {
            while (!closing) {
                TcpClient client = source.AcceptTcpClient();
                if (clients.WaitOne(0)) ThreadPool.QueueUserWorkItem(delegate {
                    using (client) {
                        try { Serve(client); }
                        catch (Exception e) { if (!closing) Note("控制连接结束："+e.Message); }
                        finally { clients.Release(); }
                    }
                });
                else client.Close();
            }
        } catch (SocketException e) { if (!closing) Note(e.Message); }
          catch (ObjectDisposedException) {}
    }
    internal static string ReadHeader(NetworkStream stream)
    {
        MemoryStream data = new MemoryStream();
        uint tail = 0;
        while (data.Length < 16384) {
            int b = stream.ReadByte();
            if (b < 0) return null;
            data.WriteByte((byte)b);
            tail = (tail << 8) | (uint)b;
            if (tail == 0x0d0a0d0a) return Encoding.ASCII.GetString(data.ToArray());
        }
        throw new IOException("Header too long");
    }
    void Serve(TcpClient client)
    {
        client.ReceiveTimeout = 65000; client.SendTimeout = 8000;
        NetworkStream stream = client.GetStream();
        PairSession pair = new PairSession();
        using (var mirror = new Mirror(client, Note, viewer))
        try {
        while (!closing) {
            string header = ReadHeader(stream);
            if (header == null) return;
            string[] lines = header.Split(new string[]{"\r\n"}, StringSplitOptions.None);
            string[] request = lines[0].Split(' ');
            if (request.Length != 3) return;
            string cseq = "", proto = request[2] == "RTSP/1.0" ? "RTSP/1.0" : "HTTP/1.1";
            int size = 0;
            string contentType="";
            foreach (string line in lines) {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string key = line.Substring(0, colon).Trim().ToLowerInvariant(), val = line.Substring(colon + 1).Trim();
                if (key == "content-length" && (!int.TryParse(val, out size) || size < 0 || size > 1048576)) return;
                if (key == "content-type") contentType=val.Split(';')[0].Trim();
                uint seq;
                if (key == "cseq" && uint.TryParse(val, out seq)) cseq = "CSeq: " + seq + "\r\n";
            }
            byte[] payload = new byte[size];
            while (size > 0) {
                int n = stream.Read(payload, payload.Length - size, size);
                if (n == 0) return;
                size -= n;
            }
            string path = request[1].Split('?')[0], code = "200 OK", extra = "";
            byte[] body = new byte[0];
            if (request[0] == "GET" && path == "/info") {
                var display=viewer.DesiredDisplay;
                Note("镜像协商目标 "+display.Width+" × "+display.Height+"，最高 "+display.Fps+"fps");
                body = Plist.Write(new Dictionary<string,object>{
                    {"deviceID",device},{"name",serviceName},{"model",Model},{"sourceVersion",Version},
                    {"features",0x5A7FFFF7},{"statusFlags",4},{"protocolVersion","1.0"},{"pk",publicKey},{"vv",2},
                    {"audioFormats",new object[]{
                        new Dictionary<string,object>{{"type",100},{"audioInputFormats",0},{"audioOutputFormats",(1<<11)|(1<<22)|(1<<24)}},
                        new Dictionary<string,object>{{"type",101},{"audioInputFormats",0},{"audioOutputFormats",(1<<11)|(1<<22)|(1<<24)}}
                    }},
                    {"displays",new object[]{new Dictionary<string,object>{
                        {"uuid","4ed36a40-3af2-4be2-91b9-93e3f35f22c3"},{"width",display.Width},{"height",display.Height},
                        {"widthPixels",display.Width},{"heightPixels",display.Height},{"rotation",false},{"refreshRate",1.0/display.Fps},
                        {"maxFPS",display.Fps},{"overscanned",false},{"features",14}
                    }}}
                });
                extra = "Content-Type: application/x-apple-binary-plist\r\n";
            } else if (request[0] == "GET" && path == "/server-info") {
                body = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>deviceid</key><string>" + device +
                    "</string><key>features</key><integer>1518338039</integer><key>model</key><string>" + Model +
                    "</string><key>protovers</key><string>1.0</string><key>srcvers</key><string>" + Version + "</string></dict></plist>");
                extra = "Content-Type: text/x-apple-plist+xml\r\n";
            } else if (request[0] == "POST" && path == "/pair-setup" && payload.Length == 32) {
                body = publicKey;
                extra = "Content-Type: application/octet-stream\r\n";
            } else if (request[0] == "POST" && path == "/pair-verify") {
                try { body = pair.Verify(payload,identity); }
                catch (System.Security.Cryptography.CryptographicException) { code = "403 Forbidden"; }
                extra = "Content-Type: application/octet-stream\r\n";
            } else if (request[0] == "POST" && path == "/fp-setup") {
                body = mirror.FairPlay(payload);
                extra = "Content-Type: application/octet-stream\r\n";
            } else if (request[0] == "SETUP") {
                body = mirror.Setup(payload,pair.VerifiedSecret);
                // An active mirror can carry media without issuing RTSP commands.
                // Probe the TCP peer instead of expiring the session on RTSP silence.
                byte[] keepAlive=new byte[12];
                BitConverter.GetBytes(1).CopyTo(keepAlive,0);
                BitConverter.GetBytes(30000).CopyTo(keepAlive,4);
                BitConverter.GetBytes(3000).CopyTo(keepAlive,8);
                client.Client.IOControl(IOControlCode.KeepAliveValues,keepAlive,null);
                client.ReceiveTimeout=0;
                extra = "Content-Type: application/x-apple-binary-plist\r\nSession: 1\r\n";
            } else if (request[0] == "OPTIONS") extra = "Public: SETUP, RECORD, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER\r\n";
            else if (request[0] == "RECORD") extra = "Audio-Latency: 11025\r\n";
            else if (request[0] == "GET_PARAMETER" && Encoding.ASCII.GetString(payload).Contains("volume")) {
                body=Encoding.ASCII.GetBytes("volume: "+mirror.SenderVolumeDb.ToString("F6",System.Globalization.CultureInfo.InvariantCulture)+"\r\n"); extra="Content-Type: text/parameters\r\n";
            } else if(request[0]=="SET_PARAMETER" && (contentType.Length==0 || contentType.Equals("text/parameters",StringComparison.OrdinalIgnoreCase))){
                if(!mirror.SetParameters(Encoding.ASCII.GetString(payload)))code="400 Bad Request";
            } else if(request[0]=="FLUSH") mirror.FlushAudio();
            else if(request[0]=="TEARDOWN") mirror.Teardown(payload);
            else if (request[0]=="GET_PARAMETER" || request[0]=="SET_PARAMETER" || (request[0]=="POST" && path=="/feedback")) {}
            else code = "501 Not Implemented";
            if(path!="/feedback" && request[0]!="SET_PARAMETER") Note(client.Client.RemoteEndPoint + "  " + request[0] + " " + path + "  " + code);
            byte[] response = Encoding.ASCII.GetBytes(proto + " " + code + "\r\n" + cseq + "Server: AirTunes/" + Version +
                "\r\n" + extra + "Content-Length: " + body.Length + "\r\n\r\n");
            stream.Write(response, 0, response.Length);
            stream.Write(body, 0, body.Length);
            if (code != "200 OK") return;
        }
    }
        finally { pair.Clear(); }
    }
    Receiver()
    {
        Text = "iScreenX · AirPlay 连接诊断";
        Size = new Size(740, 440);
        Controls.Add(log); Controls.Add(status);
        ShowInTaskbar = false; Opacity = 0;
        Shown += delegate { try { Start(); Hide(); } catch (Exception e) { MessageBox.Show(e.Message, "iScreenX"); Close(); } };
        FormClosing += delegate { Shutdown(); };
    }
    [STAThread] static void Main(string[] args) {
        if (SshConnectionCheck.AnswerPasswordPrompt(args)) return;
        Application.EnableVisualStyles(); Application.Run(new Receiver());
    }
}

