using System;
using System.IO;
using System.Runtime.InteropServices;

internal sealed class AacDecoder : IDisposable
{
    const string Library="iscreenx-aac.dll";
    [DllImport(Library)] static extern IntPtr isx_aac_open(int codec);
    [DllImport(Library)] static extern int isx_aac_frame(IntPtr decoder,byte[] input,uint size,[Out] byte[] output,int sampleCapacity);
    [DllImport(Library)] static extern void isx_aac_close(IntPtr decoder);

    IntPtr handle;
    internal readonly byte[] Samples=new byte[8192];
    internal AacDecoder(int codec){
        handle=isx_aac_open(codec);
        if(handle==IntPtr.Zero)throw new IOException("AAC 解码器初始化失败");
    }
    internal int Decode(byte[] packet){
        return isx_aac_frame(handle,packet,(uint)packet.Length,Samples,Samples.Length/2);
    }
    internal int Conceal(){return isx_aac_frame(handle,null,0,Samples,Samples.Length/2);}
    public void Dispose(){if(handle!=IntPtr.Zero){isx_aac_close(handle);handle=IntPtr.Zero;}}
}
