using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal sealed class Mirror : IDisposable
{
    [DllImport("iscreenx-fp.dll")] static extern void fp_reply(uint mode,[Out] byte[] output);
    [DllImport("iscreenx-fp.dll")] static extern void fp_key(byte[] message,byte[] wrapped,[Out] byte[] key);
    readonly IPAddress local, peer;
    readonly Action<string> note;
    readonly List<IDisposable> resources=new List<IDisposable>();
    byte[] handshake,sessionKey;
    bool fpStarted;
    volatile bool closed;
    internal Mirror(TcpClient control,Action<string> report) {
        local=((IPEndPoint)control.Client.LocalEndPoint).Address;peer=((IPEndPoint)control.Client.RemoteEndPoint).Address;note=report;
    }
    internal byte[] FairPlay(byte[] data){
        if(data.Length<16 || Encoding.ASCII.GetString(data,0,4)!="FPLY" || data[4]!=3 || data[5]!=1)throw new InvalidDataException("FairPlay 版本或消息格式不符");
        if(data.Length==16 && data[6]==1 && data[14]<4){
            var r=new byte[142];fp_reply(data[14],r);fpStarted=true;return r;
        }
        if(fpStarted && data.Length==164 && data[6]==3 && data[12]<4){
            handshake=data;
            return new byte[]{70,80,76,89,3,1,4,0,0,0,0,20}.Concat(data.Skip(144)).ToArray();
        }
        throw new InvalidDataException("FairPlay 握手顺序错误");
    }
    static byte[] Hash(byte[] a,byte[] b){
        using(var h=SHA512.Create())return h.ComputeHash(a.Concat(b).ToArray()).Take(16).ToArray();
    }
    T Track<T>(T resource) where T:IDisposable{
        lock(resources){if(closed){resource.Dispose();throw new ObjectDisposedException("Mirror");}resources.Add(resource);return resource;}
    }
    void Run(Action task){
        ThreadPool.QueueUserWorkItem(delegate{
            try{task();}catch(Exception e){if(!closed)note("串流 · "+e.Message);}
        });
    }
    int Tcp(Action<TcpClient> run){
        var socket=Track(new Socket(local.AddressFamily,SocketType.Stream,ProtocolType.Tcp));
        socket.Bind(new IPEndPoint(local,0));socket.Listen(2);
        Run(delegate {
            while(!closed) {
                var accepted=socket.Accept();
                if(!peer.Equals(((IPEndPoint)accepted.RemoteEndPoint).Address)){accepted.Close();continue;}
                using(var c=Track(new TcpClient(local.AddressFamily))){
                    c.Client.Close();c.Client=accepted;c.NoDelay=true;run(c);
                }
                break;
            }
        });
        return ((IPEndPoint)socket.LocalEndPoint).Port;
    }
    UdpClient Udp(){return Track(new UdpClient(new IPEndPoint(local,0)));}
    static int Port(UdpClient u){return ((IPEndPoint)u.Client.LocalEndPoint).Port;}
    void TimeSync(UdpClient u,int port){
        u.Connect(peer,port);
        Run(delegate {
            byte[] req=new byte[32];req[0]=0x80;req[1]=0xd2;req[3]=7;
            while(!closed){
                long ticks=DateTime.UtcNow.Ticks-new DateTime(1900,1,1).Ticks;
                ulong t=((ulong)(ticks/10000000)<<32)+((ulong)(ticks%10000000)<<32)/10000000;
                for(int i=0;i<8;i++)req[31-i]=(byte)(t>>(8*i));
                u.Send(req,req.Length);Thread.Sleep(1000);
                while(!closed && u.Available>0){IPEndPoint from=null;u.Receive(ref from);}
            }
        });
    }
    internal byte[] Setup(byte[] payload,byte[] secret){
        var req=Plist.Read(payload);var reply=new Dictionary<string,object>();
        if(req.ContainsKey("ekey")){
            byte[] wrapped=req["ekey"] as byte[];
            if(handshake==null || wrapped==null || wrapped.Length!=72)throw new InvalidDataException("会话密钥无效");
            byte[] key=new byte[16];fp_key(handshake,wrapped,key);
            sessionKey=secret==null?key:Hash(key,secret);
            var timing=Udp();reply["timingPort"]=Port(timing);
            if(req.ContainsKey("timingPort"))TimeSync(timing,Convert.ToInt32(req["timingPort"]));
            reply["eventPort"]=Tcp(delegate(TcpClient c){var stream=c.GetStream();while(Receiver.ReadHeader(stream)!=null){
                byte[] ok=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");stream.Write(ok,0,ok.Length);
            }});
        }
        if(req.ContainsKey("streams")){
            var replies=new List<object>();
            foreach(var value in (object[])req["streams"]){
                var stream=(Dictionary<string,object>)value;
                int type=Convert.ToInt32(stream["type"]);var r=new Dictionary<string,object>{{"type",type}};
                if(type==110){
                    string id=((ulong)stream["streamConnectionID"]).ToString(CultureInfo.InvariantCulture);
                    if(sessionKey==null)throw new InvalidDataException("缺少视频密钥");
                    byte[] key=Hash(Encoding.ASCII.GetBytes("AirPlayStreamKey"+id),sessionKey);
                    byte[] iv=Hash(Encoding.ASCII.GetBytes("AirPlayStreamIV"+id),sessionKey);
                    r["dataPort"]=Tcp(delegate(TcpClient c){Video(c,key,iv);});
                }else if(type==96){
                    // Reserve genuine RTP ports for the phone's accompanying audio session.
                    r["dataPort"]=Port(Udp());r["controlPort"]=Port(Udp());
                }else throw new InvalidDataException("未知串流类型 "+type);
                replies.Add(r);
            }
            reply["streams"]=replies.ToArray();
        }
        return Plist.Write(reply);
    }
    static bool Read(Stream s,byte[] b){
        for(int p=0;p<b.Length;){int n=s.Read(b,p,b.Length-p);if(n==0)return false;p+=n;}return true;
    }
    void Video(TcpClient client,byte[] key,byte[] counter){
        note("已连接视频通道");
        using(var viewer=Track(new Viewer(note)))
        using(var aes=Aes.Create()){
            aes.Mode=CipherMode.ECB;aes.Padding=PaddingMode.None;aes.Key=key;
            using(var cipher=aes.CreateEncryptor()){
                // Encrypt batches of sequential counters; unused keystream carries across frames.
                byte[] counters=new byte[16384],pad=new byte[counters.Length],h=new byte[128];int used=pad.Length;
                var s=client.GetStream();
                while(!closed && Read(s,h)){
                    int n=BitConverter.ToInt32(h,0);if(n<0||n>16*1024*1024)throw new InvalidDataException("视频包长度异常");
                    byte[] b=new byte[n];if(!Read(s,b))break;
                    if(h[4]==0){
                        for(int p=0;p<n;){
                            if(used==pad.Length){
                                for(int i=0;i<counters.Length;i+=16){
                                    Buffer.BlockCopy(counter,0,counters,i,16);
                                    for(int j=15;j>=0 && ++counter[j]==0;j--){}
                                }
                                cipher.TransformBlock(counters,0,counters.Length,pad,0);used=0;
                            }
                            int len=Math.Min(n-p,pad.Length-used);
                            for(int i=0;i<len;i++)b[p+i]^=pad[used+i];p+=len;used+=len;
                        }
                    }else if(h[4]!=1)continue;
                    if(n>0)viewer.Send(h[4],b,BitConverter.ToUInt64(h,8));
                }
            }
        }
    }
    public void Dispose(){
        lock(resources){closed=true;foreach(var r in resources)r.Dispose();resources.Clear();}
        if(sessionKey!=null)Array.Clear(sessionKey,0,sessionKey.Length);
    }
}
