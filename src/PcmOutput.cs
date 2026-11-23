using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;

// Small rotating set of interleaved stereo PCM buffers owned by the Windows audio device.
internal sealed class PcmOutput : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack=2)]
    struct Format { public ushort tag, channels; public uint rate, bytesPerSecond; public ushort align,bits,extra; }
    [StructLayout(LayoutKind.Sequential)]
    struct Header { public IntPtr data; public uint length,recorded; public UIntPtr user; public uint flags, loops; public IntPtr next,reserved; }
    [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr dev,uint id,ref Format fmt,IntPtr cb,IntPtr instance,uint flags);
    [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr dev,IntPtr hdr,int size);
    [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr dev,IntPtr hdr,int size);
    [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr dev,IntPtr hdr,int size);
    [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr dev);
    [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr dev);

    [DllImport("winmm.dll")] static extern uint waveOutPause(IntPtr dev);
    [DllImport("winmm.dll")] static extern uint waveOutRestart(IntPtr dev);
    readonly IntPtr[] headers=new IntPtr[56], samples=new IntPtr[56];
    readonly AutoResetEvent completed=new AutoResetEvent(false);
    int prefillSamples; readonly int rate;
    internal int DelayMs {get {return prefillSamples*1000/rate;}}
    int bufferedSamples;
    bool playing;
    internal int Underruns, Stalls;
    int PreviousSlot { get {return (cursor+headers.Length-1)%headers.Length;} }
    bool InQueue(int slot){return (Marshal.ReadInt32(headers[slot],FlagsOffset)&16)!=0;}
    static readonly int FlagsOffset=(int)Marshal.OffsetOf(typeof(Header),"flags");
    readonly int size=Marshal.SizeOf(typeof(Header));
    IntPtr device;
    int cursor;
    const int Capacity=8192;
    internal PcmOutput(int rate,int initialDelay=145) {
        var fmt=new Format {tag=1,channels=2,rate=(uint)rate,bytesPerSecond=(uint)rate*4,align=4,bits=16};
        this.rate=rate;prefillSamples=rate*initialDelay/1000;
        Check(waveOutOpen(out device,uint.MaxValue,ref fmt,completed.SafeWaitHandle.DangerousGetHandle(),IntPtr.Zero,0x50000));
        try {
            Check(waveOutPause(device));
            for(int i=0;i<headers.Length;i++){
                samples[i]=Marshal.AllocHGlobal(Capacity);headers[i]=Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(new Header{data=samples[i],length=Capacity},headers[i],false);
                Check(waveOutPrepareHeader(device,headers[i],size));
            }
        }catch { Dispose(); throw; }
    }
    static void Check(uint result){if(result!=0)throw new IOException("Windows 音频输出错误 "+result);}
    internal bool Write(byte[] pcm,int length,float gain) {
        if(length==0 || length>Capacity || length%4!=0)return false;
        IntPtr h=headers[cursor];
        if(InQueue(cursor)){
            var wait=Stopwatch.StartNew();
            while(InQueue(cursor)){
                if(wait.ElapsedMilliseconds>=200){Stalls++;Flush();break;}
                completed.WaitOne(20);
            }
            h=headers[cursor];
        }
        if(playing && !InQueue(PreviousSlot)){
            Underruns++;prefillSamples=Math.Min(rate*460/1000,prefillSamples+rate*90/1000);
            Check(waveOutPause(device));playing=false;bufferedSamples=0;
        }
        if(gain!=100){
            for(int i=0;i<length;i+=2){
                int v=(int)((short)(pcm[i]|pcm[i+1]<<8)*gain/100);
                pcm[i]=(byte)v;pcm[i+1]=(byte)(v>>8);
            }
        }
        Marshal.Copy(pcm,0,samples[cursor],length);
        Marshal.WriteInt32(h,IntPtr.Size,length);
        Check(waveOutWrite(device,h,size));cursor=(cursor+1)%headers.Length;
        if(!playing){
            bufferedSamples+=length/4;
            if(bufferedSamples>=prefillSamples){Check(waveOutRestart(device));playing=true;}
        }
        return true;
    }
    internal void Flush(){
        Check(waveOutReset(device));Check(waveOutPause(device));
        cursor=0;bufferedSamples=0;playing=false;
    }
    public void Dispose(){
        if(device==IntPtr.Zero)return;
        waveOutReset(device);
        for(int i=0;i<headers.Length;i++){
            if(headers[i]!=IntPtr.Zero){waveOutUnprepareHeader(device,headers[i],size);Marshal.FreeHGlobal(headers[i]);}
            if(samples[i]!=IntPtr.Zero)Marshal.FreeHGlobal(samples[i]);
        }
        waveOutClose(device);device=IntPtr.Zero;completed.Dispose();
    }
}
