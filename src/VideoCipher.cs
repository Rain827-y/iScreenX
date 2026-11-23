using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

// The same system crypto provider used for AirPlay pairing supports streaming CTR.
internal sealed class VideoCipher : IDisposable
{
    const string Lib="libcrypto.dll";
    [DllImport(Lib)] static extern IntPtr EVP_CIPHER_CTX_new();
    [DllImport(Lib)] static extern IntPtr EVP_aes_128_ctr();
    [DllImport(Lib)] static extern int EVP_DecryptInit_ex(IntPtr ctx,IntPtr cipher,IntPtr engine,byte[] key,byte[] counter);
    [DllImport(Lib)] static extern int EVP_DecryptUpdate(IntPtr ctx,IntPtr output,out int written,IntPtr input,int length);
    [DllImport(Lib)] static extern void EVP_CIPHER_CTX_free(IntPtr ctx);
    IntPtr handle;
    internal VideoCipher(byte[] key,byte[] counter){
        handle=EVP_CIPHER_CTX_new();
        if(handle==IntPtr.Zero || EVP_DecryptInit_ex(handle,EVP_aes_128_ctr(),IntPtr.Zero,key,counter)!=1){
            Dispose();throw new CryptographicException("无法初始化视频解密");
        }
    }
    internal void Transform(byte[] data){
        if(data.Length==0)return;
        var pinned=GCHandle.Alloc(data,GCHandleType.Pinned);
        try {
            IntPtr address=pinned.AddrOfPinnedObject();int written;
            if(EVP_DecryptUpdate(handle,address,out written,address,data.Length)!=1 || written!=data.Length)
                throw new CryptographicException("视频解密失败");
        } finally {pinned.Free();}
    }
    public void Dispose(){if(handle!=IntPtr.Zero){EVP_CIPHER_CTX_free(handle);handle=IntPtr.Zero;}}
}
