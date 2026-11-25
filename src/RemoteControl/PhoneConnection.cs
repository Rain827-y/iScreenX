using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

internal sealed class PhoneConnection : IDisposable
{
    readonly object gate = new object();
    Process active;
    readonly LinkedList<string> commands = new LinkedList<string>();
    bool pressed;
    int orientation;
    string state = "idle:";
    internal string Status { get { lock(gate) return orientation+":"+state; } }
    const string PasswordVariable = "ISCREENX_SSH_ASKPASS_SECRET";
    const string StreamEnded = "ISCREENX_STREAM_ENDED";

    // Windows OpenSSH launches this executable as SSH_ASKPASS with a single prompt.
    internal static bool AnswerPasswordPrompt(string[] args)
    {
        string password = Environment.GetEnvironmentVariable(PasswordVariable);
        if (password == null) return false;
        if (args.Length != 1 || !args[0].TrimEnd().EndsWith("password:", StringComparison.OrdinalIgnoreCase)) {
            Environment.ExitCode = 1;
            return true;
        }
        byte[] answer = Encoding.UTF8.GetBytes(password + "\n");
        try { using (var output = Console.OpenStandardOutput()) output.Write(answer, 0, answer.Length); }
        finally { Array.Clear(answer, 0, answer.Length); }
        return true;
    }

    internal void Connect(IPAddress address, int port, string password)
    {
        lock (gate) {
            if (active != null) return;
            if (address == null) { state="error:请先连接手机投屏。"; return; }
            if (address.AddressFamily != AddressFamily.InterNetwork) { state="error:无法获取手机的 IPv4 地址。"; return; }
            if (port<1 || port>65535 || String.IsNullOrEmpty(password)) { state="error:请检查 SSH 端口和密码。"; return; }
            var process = new Process { StartInfo = StartInfo(address,port,password) };
            active=process;
            state="pending:正在连接手机…";
            ThreadPool.QueueUserWorkItem(delegate { MonitorSession(process); });
        }
    }

    static ProcessStartInfo StartInfo(IPAddress address,int port,string password)
    {
        var start = new ProcessStartInfo {
            FileName = Path.Combine(Environment.SystemDirectory, @"OpenSSH\ssh.exe"),
            Arguments = "-F none -4 -T -o ConnectTimeout=5 -o ConnectionAttempts=1 -o NumberOfPasswordPrompts=1 " +
                "-o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o HostKeyAlgorithms=+ssh-rsa " +
                "-o ServerAliveInterval=10 -o ServerAliveCountMax=3 -p " + port + " root@" + address +
                " echo ISCREENX_READY; exec /bin/sh",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.EnvironmentVariables["SSH_ASKPASS"] = System.Windows.Forms.Application.ExecutablePath;
        start.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
        start.EnvironmentVariables["DISPLAY"] = "iscreenx";
        start.EnvironmentVariables[PasswordVariable] = password;
        return start;
    }

    void MonitorSession(Process process)
    {
        string finalState="error:SSH 连接已中断。";
        try {
            lock(gate) {
                if(active!=process)return;
                process.Start();
                process.StartInfo.EnvironmentVariables.Remove(PasswordVariable);
            }
            var errors=process.StandardError.ReadToEndAsync();
            var ready=process.StandardOutput.ReadLineAsync();
            var deadline=Stopwatch.StartNew();
            while(true) {
                int remaining=(int)(10000-deadline.ElapsedMilliseconds);
                if(remaining<=0 || !ready.Wait(remaining)) {
                    finalState="error:SSH 登录超时，请检查手机网络和端口。"; return;
                }
                if(ready.Result==null)break;
                if(ready.Result=="ISCREENX_READY") {
                    RunTouchSession(process);
                    return;
                }
                ready=process.StandardOutput.ReadLineAsync();
            }
            if(process.WaitForExit(1000))finalState="error:"+LoginError(errors.Result.ToLowerInvariant());
        } catch(Win32Exception) { finalState="error:无法运行 Windows OpenSSH 客户端。"; }
          catch(TimeoutException error) { finalState="error:"+error.Message; }
          catch(Exception) { finalState="error:SSH 通信中断，请重新连接。"; }
        finally {
            lock(gate) {
                Stop(process);
                if(active==process){Reset();state=finalState;}
            }
            process.Dispose();
        }
    }

    void SetState(Process process,string value)
    {
        lock(gate) {
            if(active!=process)throw new OperationCanceledException();
            state=value;
        }
    }

    void RunTouchSession(Process process)
    {
        process.StandardInput.NewLine="\n";
        SetState(process,"working:正在连接 iTouch…");
        bool streaming=OpenTouchStream(process);
        var keepalive=Stopwatch.StartNew();
        if(!streaming)ReportUnavailable(process);
        while (true) {
            string command;
            lock(gate) {
                if(active==process && commands.Count==0)Monitor.Wait(gate,250);
                if(active!=process || process.HasExited)return;
                command=commands.First==null?null:commands.First.Value;
                if(command!=null)commands.RemoveFirst();
            }
            if(streaming) {
                if(command==null && keepalive.ElapsedMilliseconds<750)continue;
                process.StandardInput.WriteLine(command ?? "ping");
                string reply=ReadTouchReply(process,6000,"等待触摸确认超时，请重新连接手机。");
                keepalive.Restart();
                if(reply=="OK") {
                    if(command=="ping")SetState(process,"ready:反控检测通过。");
                } else {
                    // A device-side timeout can end the stream while leaving the SSH shell usable.
                    if(reply!=StreamEnded && (reply==null || !reply.StartsWith("ERR ",StringComparison.Ordinal) ||
                        ReadTouchReply(process,6000,"等待触摸流退出超时，请重新连接手机。")!=StreamEnded))
                        throw new IOException("Unexpected iTouch reply");
                    lock(gate) {
                        if(active!=process)return;
                        commands.Clear();pressed=false;orientation=0;
                        state="working:正在恢复 iTouch…";
                    }
                    streaming=OpenTouchStream(process);
                    if(!streaming)ReportUnavailable(process);
                }
            } else if(command!=null) {
                if(command=="install" && !InstallPackage(process)) {
                    SetState(process,"service-error:iTouch 安装失败，请检查手机上的 dpkg 和服务状态。");
                    continue;
                }
                int attempts=command=="install"?5:1;
                for(int i=0;i<attempts && !streaming;i++) {
                    if(i>0)Thread.Sleep(250);
                    streaming=OpenTouchStream(process);
                }
                if(!streaming)ReportUnavailable(process);
            }
        }
    }

    bool OpenTouchStream(Process process)
    {
        var input=process.StandardInput;
        // SpringBoard can delay its reply while the phone is locked or rotating.
        // Keep the orientation query independent of the touch stream handshake.
        input.WriteLine("version=$(dpkg-query -W -f='${Version}' com.itouch.remote 2>/dev/null); " +
            "if dpkg --compare-versions \"$version\" ge 1.1.1; then " +
            "( last=; while :; do current=$(/usr/local/bin/itouch orientation 2>/dev/null); " +
            "if [ \"$current\" != \"$last\" ]; then printf 'ISCREENX_DIRECTION:%s\\n' \"$current\"; last=$current; fi; " +
            "sleep 0.25 2>/dev/null || sleep 1; done ) </dev/null & " +
            "monitor=$!; trap 'kill \"$monitor\" 2>/dev/null' EXIT; /usr/local/bin/itouch stream; " +
            "kill \"$monitor\" 2>/dev/null; wait \"$monitor\" 2>/dev/null; trap - EXIT; fi; echo "+StreamEnded);
        for(int i=0;i<8;i++) {
            string response=ReadTouchReply(process,6000,"启动 iTouch 触摸流超时，请重新连接手机。");
            if(response=="OK stream/1") {
                SetState(process,"ready:iTouch 已就绪，可以操作投屏画面。");
                return true;
            }
            if(response==StreamEnded)return false;
            if(response==null)break;
        }
        throw new IOException("Unexpected iTouch handshake");
    }

    bool UpdateOrientation(Process process,string line)
    {
        const string prefix="ISCREENX_DIRECTION:";
        if(line==null || !line.StartsWith(prefix,StringComparison.Ordinal))return false;
        int next;
        if(!Int32.TryParse(line.Substring(prefix.Length),NumberStyles.None,CultureInfo.InvariantCulture,out next) || next<1 || next>4)
            next=0;
        lock(gate) {
            if(active!=process)throw new OperationCanceledException();
            if(orientation!=next) {
                orientation=next;
                if(state.StartsWith("ready:",StringComparison.Ordinal)) {
                    commands.Clear();
                    commands.AddLast("up");
                    pressed=false;
                }
            }
        }
        return true;
    }

    string ReadTouchReply(Process process,int timeout,string timeoutMessage)
    {
        var elapsed=Stopwatch.StartNew();
        while(true) {
            int remaining=timeout-(int)elapsed.ElapsedMilliseconds;
            if(remaining<=0)throw new TimeoutException(timeoutMessage);
            string response=ReadLine(process,remaining,timeoutMessage);
            if(!UpdateOrientation(process,response))return response;
        }
    }

    void ReportUnavailable(Process process)
    {
        SetState(process,"service-error:请安装 iTouch 1.1.1，或解锁手机后重试反控检测。");
    }

    static string ReadLine(Process process,int timeout,string timeoutMessage)
    {
        var reply=process.StandardOutput.ReadLineAsync();
        if(!reply.Wait(timeout))throw new TimeoutException(timeoutMessage);
        return reply.Result;
    }

    static bool InstallPackage(Process process)
    {
        using(var package=typeof(PhoneConnection).Assembly.GetManifestResourceStream("itouch.deb")){
            if(package==null)return false;
            var input=process.StandardInput;
            input.WriteLine("umask 077; pkg=$(mktemp /var/tmp/itouch.XXXXXX) || exit 1; trap 'rm -f \"$pkg\"' EXIT");
            byte[] buffer=new byte[512];
            int count;
            while((count=package.Read(buffer,0,buffer.Length))>0) {
                var octal=new StringBuilder(count*5);
                for(int i=0;i<count;i++)octal.Append("\\0").Append(Convert.ToString(buffer[i],8).PadLeft(3,'0'));
                input.WriteLine("printf '%b' '"+octal+"' >> \"$pkg\" || exit 1");
            }
            input.WriteLine("if dpkg -i \"$pkg\" >&2; then echo ISCREENX_INSTALLED; else echo ISCREENX_INSTALL_FAILED; fi; rm -f \"$pkg\"; trap - EXIT");
            return ReadLine(process,30000,"等待 iTouch 安装结果超时，请检查手机服务。")=="ISCREENX_INSTALLED";
        }
    }

    internal void ServiceAction(bool install)
    {
        lock(gate) {
            if(state.StartsWith("service-error:",StringComparison.Ordinal)) {
                state=install?"working:正在安装 iTouch…":"working:正在检测 iTouch…";
                commands.AddLast(install?"install":"ping");
            } else if(!install && state.StartsWith("ready:",StringComparison.Ordinal) && commands.Count<32) {
                commands.AddLast("ping");
            }
            Monitor.Pulse(gate);
        }
    }

    internal void Touch(string action,int expectedOrientation,double x,double y)
    {
        lock(gate) {
            if(!state.StartsWith("ready:",StringComparison.Ordinal))return;
            if(action=="up") {
                if(!pressed)return;
                pressed=false;
                commands.AddLast("up");
            } else {
                if(orientation==0 || orientation!=expectedOrientation || (action!="down" && action!="move"))return;
                bool down=action=="down";
                if(down==pressed || !(x>=0 && x<=1 && y>=0 && y<=1))return;
                string line=String.Format(CultureInfo.InvariantCulture,"{0} {1:F6} {2:F6}",action,x,y);
                if(!down && commands.Last!=null && commands.Last.Value.StartsWith("move ",StringComparison.Ordinal))
                    commands.Last.Value=line;
                else if(commands.Count<32)
                    commands.AddLast(line);
                else return;
                pressed=true;
            }
            Monitor.Pulse(gate);
        }
    }

    static string LoginError(string error)
    {
        if (error.Contains("host key verification failed") || error.Contains("host identification has changed"))
            return "SSH 主机密钥验证失败，请核对手机身份。";
        if (error.Contains("permission denied")) return "SSH 登录失败，请检查root密码和登录权限。";
        if (error.Contains("connection refused")) return "手机拒绝连接，请检查 SSH 服务和端口。";
        if (error.Contains("no matching")) return "手机的 SSH 算法与 Windows 客户端不兼容。";
        return "SSH 连接失败，请检查手机网络、端口和 SSH 服务。";
    }

    static void Stop(Process process)
    {
        try { if(!process.HasExited)process.Kill(); }
        catch(InvalidOperationException) {} // Worker may not have started yet.
    }

    internal void Disconnect()
    {
        lock(gate) {
            if(active!=null)Stop(active);
            Reset();
        }
    }

    void Reset()
    {
        active=null;state="idle:";
        commands.Clear();pressed=false;orientation=0;
        Monitor.Pulse(gate);
    }

    public void Dispose(){ Disconnect(); }
}
