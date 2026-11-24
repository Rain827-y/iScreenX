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
    byte[] handshake,sessionKey,audioIv;
    readonly Viewer viewer;
    internal float SenderVolumeDb { get; private set; }
    volatile float senderGain=1;
    float PlaybackVolume { get { return viewer.AudioVolume*senderGain; } }
    internal bool SetParameters(string parameters){
        foreach(string line in parameters.Split('\n')){
            int colon=line.IndexOf(':');
            if(colon<0 || !line.Substring(0,colon).Trim().Equals("volume",StringComparison.OrdinalIgnoreCase))continue;
            float db;
            if(!float.TryParse(line.Substring(colon+1),NumberStyles.Float,CultureInfo.InvariantCulture,out db)
                || float.IsNaN(db) || float.IsInfinity(db) || db>0 || db< -144)return false;
            SenderVolumeDb=db;
            senderGain=db<=-144?0:(float)Math.Pow(10,db/20.0);
            note("手机音量 "+db.ToString("0.##",CultureInfo.InvariantCulture)+" dB");
        }
        return true;
    }
    string senderName="iOS";
    UdpClient audioData,audioControl;
    int audioEpoch;
    bool fpStarted;
    volatile bool closed;
    internal Mirror(TcpClient control,Action<string> report,Viewer display) {
        local=((IPEndPoint)control.Client.LocalEndPoint).Address;peer=((IPEndPoint)control.Client.RemoteEndPoint).Address;note=report;viewer=display;
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
    void Run(string channel,Action task){
        ThreadPool.QueueUserWorkItem(delegate{
            try{task();}catch(Exception e){if(!closed)note(channel+" · "+e.Message);}
        });
    }
    int Tcp(string channel,Action<TcpClient> run){
        var socket=Track(new Socket(local.AddressFamily,SocketType.Stream,ProtocolType.Tcp));
        socket.Bind(new IPEndPoint(local,0));socket.Listen(2);
        Run(channel,delegate {
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
    double phoneToPcMs=double.NaN;
    static double UnixMs(){return (DateTime.UtcNow.Ticks-621355968000000000L)/10000.0;}
    static ulong NtpNow(){
        long ticks=DateTime.UtcNow.Ticks-new DateTime(1900,1,1).Ticks;
        return ((ulong)(ticks/10000000)<<32)+((ulong)(ticks%10000000)<<32)/10000000;
    }
    static ulong ReadNtp(byte[] b,int pos){
        ulong value=0;for(int i=0;i<8;i++)value=(value<<8)|b[pos+i];return value;
    }
    static void WriteNtp(byte[] b,int pos,ulong value){
        for(int i=7;i>=0;i--){b[pos+i]=(byte)value;value>>=8;}
    }
    void TimeSync(UdpClient u,int port){
        u.Connect(peer,port);u.Client.ReceiveTimeout=500;
        Run("时钟同步",delegate {
            byte[] req=new byte[32];req[0]=0x80;req[1]=0xd2;req[3]=7;
            var sampleAge=System.Diagnostics.Stopwatch.StartNew();double bestRtt=double.MaxValue;
            while(!closed){
                ulong sent=NtpNow();WriteNtp(req,24,sent);
                u.Send(req,req.Length);
                try{
                    IPEndPoint from=null;byte[] res=u.Receive(ref from);ulong received=NtpNow();
                    if(res.Length>=32 && (res[1]&127)==0x53 && ReadNtp(res,8)==sent){
                        ulong t2=ReadNtp(res,16),t3=ReadNtp(res,24);
                        double scale=1000.0/4294967296.0;
                        double rtt=((long)(received-sent)-(long)(t3-t2))*scale;
                        if(sampleAge.ElapsedMilliseconds>30000){bestRtt=double.MaxValue;sampleAge.Restart();}
                        if(rtt>=0 && rtt<Math.Min(bestRtt,100)){
                            bestRtt=rtt;
                            Interlocked.Exchange(ref phoneToPcMs,0.5*((long)(sent-t2)*scale+(long)(received-t3)*scale));
                        }
                        WriteNtp(req,8,t3);WriteNtp(req,16,received);
                    }
                }catch(SocketException e){if(e.SocketErrorCode!=SocketError.TimedOut)throw;}
                Thread.Sleep(1000);
            }
        });
    }
    internal byte[] Setup(byte[] payload,byte[] secret){
        var req=Plist.Read(payload);var reply=new Dictionary<string,object>();
        object name;
        if(req.TryGetValue("name",out name) && name is string && !String.IsNullOrWhiteSpace((string)name))senderName=(string)name;
        if(req.ContainsKey("ekey")){
            byte[] wrapped=req["ekey"] as byte[];
            if(handshake==null || wrapped==null || wrapped.Length!=72)throw new InvalidDataException("会话密钥无效");
            audioIv=req.ContainsKey("eiv") ? req["eiv"] as byte[] : null;
            byte[] key=new byte[16];fp_key(handshake,wrapped,key);
            sessionKey=secret==null?key:Hash(key,secret);
            var timing=Udp();reply["timingPort"]=Port(timing);
            if(req.ContainsKey("timingPort"))TimeSync(timing,Convert.ToInt32(req["timingPort"]));
            reply["eventPort"]=Tcp("事件通道",delegate(TcpClient c){var stream=c.GetStream();while(Receiver.ReadHeader(stream)!=null){
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
                    r["dataPort"]=Tcp("手机视频通道",delegate(TcpClient c){Video(c,key,iv);});
                }else if(type==96){
                    int codec=stream.ContainsKey("ct")?Convert.ToInt32(stream["ct"]):1;
                    int rate=stream.ContainsKey("sr")?Convert.ToInt32(stream["sr"]):44100;
                    ulong format=stream.ContainsKey("audioFormat")?(ulong)stream["audioFormat"]:0;
                    note("音频协商 ct="+codec+" format="+format+" rate="+rate);
                    StopAudio();
                    var data=audioData=Udp();data.Client.ReceiveBufferSize=256*1024;audioControl=Udp();
                    r["dataPort"]=Port(data);r["controlPort"]=Port(audioControl);
                    Run("手机音频通道",delegate { ReceiveAudio(data,codec,rate); });
                }else throw new InvalidDataException("未知串流类型 "+type);
                replies.Add(r);
            }
            reply["streams"]=replies.ToArray();
        }
        return Plist.Write(reply);
    }
    void StopAudio(){
        lock(resources){
            if(audioData!=null){audioData.Close();resources.Remove(audioData);audioData=null;}
            if(audioControl!=null){audioControl.Close();resources.Remove(audioControl);audioControl=null;}
        }
    }
    internal void FlushAudio(){Interlocked.Increment(ref audioEpoch);}
    internal void Teardown(byte[] body){
        if(body.Length==0){StopAudio();return;}
        var request=Plist.Read(body);object streams;
        if(!request.TryGetValue("streams",out streams)){StopAudio();return;}
        foreach(var stream in (object[])streams){
            if(Convert.ToInt32(((Dictionary<string,object>)stream)["type"])==96){StopAudio();break;}
        }
    }
    void ReceiveAudio(UdpClient data,int codec,int rate){
        if((codec!=1 && codec!=8 && codec!=4) || audioIv==null || audioIv.Length!=16 || sessionKey==null)
            throw new InvalidDataException("不支持的音频会话 ct="+codec);
        if(rate!=44100)throw new InvalidDataException("不支持的采样率 "+rate);
        using(var aes=Aes.Create())
        using(var decoder=codec==1?null:new AacDecoder(codec))
        using(var output=new PcmOutput(rate)){
            aes.Mode=CipherMode.CBC;aes.Padding=PaddingMode.None;
            aes.Key=sessionKey;aes.IV=audioIv;
            int epoch=Volatile.Read(ref audioEpoch);bool reported=false;int failures=0;
            var queue=new AudioPacketQueue();
            var stats=System.Diagnostics.Stopwatch.StartNew();
            int lastSize=codec==4?4096:1920;
            byte[] silence=new byte[8192];
            while(!closed){
                IPEndPoint from=null;byte[] packet=data.Receive(ref from);
                if(!peer.Equals(from.Address)||packet.Length<=12||packet[0]!=0x80)continue;
                int nextEpoch=Volatile.Read(ref audioEpoch);
                if(epoch!=nextEpoch){queue.Reset();epoch=nextEpoch;output.Flush();}
                queue.Add(packet);
                if(stats.ElapsedMilliseconds>=15000){
                    note("音频统计：乱序恢复 "+queue.Recovered+"，缺包 "+queue.Missing+"，播放欠载 "+output.Underruns+"，输出阻塞 "+output.Stalls+"，预缓冲 "+output.DelayMs+"ms");
                    stats.Restart();
                }
                while(queue.Take(out packet)){
                    if(packet==null){
                        int recovered=decoder==null?0:decoder.Conceal();
                        if(recovered>0)output.Write(decoder.Samples,recovered,PlaybackVolume);
                        else if(reported)output.Write(silence,lastSize,0);
                        continue;
                    }
                    int length=packet.Length-12;
                    if(length==4 && packet[12]==0 && packet[13]==0x68 && packet[14]==0x34 && packet[15]==0)continue;
                    byte[] pcm=new byte[length];int encrypted=length&~15;
                    using(var decrypt=aes.CreateDecryptor()){
                        if(encrypted>0)decrypt.TransformBlock(packet,12,encrypted,pcm,0);
                    }
                    Buffer.BlockCopy(packet,12+encrypted,pcm,encrypted,length-encrypted);
                    int bytes=pcm.Length;
                    if(decoder!=null){
                        bytes=decoder.Decode(pcm);
                        if(bytes==0){
                            if(++failures==30)note("连续 30 帧音频未能解码");
                            continue;
                        }
                        failures=0;pcm=decoder.Samples;
                    }
                    lastSize=bytes;
                    bool submitted=output.Write(pcm,bytes,PlaybackVolume);
                    viewer.AudioUpdated(output.DelayMs);
                    if(submitted && !reported){
                        for(int i=0;i<bytes;i+=2){
                            if(Math.Abs((int)(short)(pcm[i]|pcm[i+1]<<8))>32){
                                note("已解码非静音音频并送入 Windows 播放设备");reported=true;break;
                            }
                        }
                    }
                }
            }
        }
    }
    static bool Read(Stream s,byte[] b){
        for(int p=0;p<b.Length;){int n=s.Read(b,p,b.Length-p);if(n==0)return false;p+=n;}return true;
    }
    void Video(TcpClient client,byte[] key,byte[] counter){
        note("已连接视频通道");
        string id=viewer.Begin(senderName,peer);
        try {
            using(var cipher=new VideoCipher(key,counter)){
                byte[] h=new byte[128];var s=client.GetStream();
                var stats=System.Diagnostics.Stopwatch.StartNew();
                int frames=0;long worstGap=0,previous=0,slowestSend=0;
                while(!closed && Read(s,h)){
                    int n=BitConverter.ToInt32(h,0);
                    if(n<0 || n>16*1024*1024)throw new InvalidDataException("视频包长度异常");
                    byte[] b=new byte[n];if(!Read(s,b))break;
                    double arrival=UnixMs();
                    long now=stats.ElapsedMilliseconds;
                    if(h[4]==0){
                        cipher.Transform(b);frames++;
                        if(previous!=0)worstGap=Math.Max(worstGap,now-previous);
                        previous=now;
                    }else if(h[4]!=1)continue;
                    if(n>0){
                        ulong stamp=BitConverter.ToUInt64(h,8);
                        double source=stamp*(1000.0/4294967296.0)+Volatile.Read(ref phoneToPcMs);
                        const double eraMs=4294967296.0*1000;
                        source+=Math.Round((arrival-source)/eraMs)*eraMs;
                        // iOS video stamps are boot-relative, timing replies include the NTP epoch.
                        // Reject incomparable clocks rather than using a fabricated capture time.
                        if(double.IsNaN(source) || Math.Abs(arrival-source)>30000)source=double.NaN;
                        viewer.Send(id,h[4],b,stamp,arrival,source);
                    }
                    slowestSend=Math.Max(slowestSend,stats.ElapsedMilliseconds-now);
                    if(now>=15000){
                        note("视频统计：收到 "+frames+" 帧 / "+now+"ms，最大帧间隔 "+worstGap+"ms，最长本地发送 "+slowestSend+"ms");
                        stats.Restart();frames=0;previous=worstGap=slowestSend=0;
                    }
                }
                note("视频数据通道已关闭");
            }
        }
        finally {viewer.End(id);}
    }
    public void Dispose(){
        lock(resources){closed=true;foreach(var r in resources)r.Dispose();resources.Clear();}
        if(sessionKey!=null)Array.Clear(sessionKey,0,sessionKey.Length);
    }
}
