using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// Ed25519 and X25519 use the crypto provider shipped with Windows OpenSSH.
// Native size_t parameters below match the x64 receiver build.
internal static class PairKeys
{
    const string Lib = "libcrypto.dll";
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibrary(string path);
    [DllImport(Lib)] static extern int OBJ_txt2nid(string name);
    [DllImport(Lib)] static extern IntPtr EVP_PKEY_new_raw_private_key(int type, IntPtr engine, byte[] key, ulong len);
    [DllImport(Lib)] static extern IntPtr EVP_PKEY_new_raw_public_key(int type, IntPtr engine, byte[] key, ulong len);
    [DllImport(Lib)] static extern int EVP_PKEY_get_raw_public_key(IntPtr key, byte[] result, ref ulong len);
    [DllImport(Lib)] static extern void EVP_PKEY_free(IntPtr key);
    [DllImport(Lib)] static extern IntPtr EVP_PKEY_CTX_new(IntPtr key, IntPtr engine);
    [DllImport(Lib)] static extern void EVP_PKEY_CTX_free(IntPtr ctx);
    [DllImport(Lib)] static extern int EVP_PKEY_derive_init(IntPtr ctx);
    [DllImport(Lib)] static extern int EVP_PKEY_derive_set_peer(IntPtr ctx, IntPtr peer);
    [DllImport(Lib)] static extern int EVP_PKEY_derive(IntPtr ctx, byte[] result, ref ulong len);
    [DllImport(Lib)] static extern IntPtr EVP_MD_CTX_new();
    [DllImport(Lib)] static extern void EVP_MD_CTX_free(IntPtr ctx);
    [DllImport(Lib)] static extern int EVP_DigestSignInit(IntPtr ctx, IntPtr pkctx, IntPtr md, IntPtr engine, IntPtr key);
    [DllImport(Lib)] static extern int EVP_DigestSign(IntPtr ctx, byte[] sig, ref ulong len, byte[] msg, ulong size);
    [DllImport(Lib)] static extern int EVP_DigestVerifyInit(IntPtr ctx, IntPtr pkctx, IntPtr md, IntPtr engine, IntPtr key);
    [DllImport(Lib)] static extern int EVP_DigestVerify(IntPtr ctx, byte[] sig, ulong len, byte[] msg, ulong size);
    static readonly IntPtr Zero = IntPtr.Zero;
    public static readonly int Ed, X;
    static PairKeys()
    {
        if (LoadLibrary(Path.Combine(Environment.SystemDirectory, Lib)) == Zero)
            throw new InvalidOperationException("找不到 Windows 系统 libcrypto.dll");
        Ed = OBJ_txt2nid("1.3.101.112"); X = OBJ_txt2nid("1.3.101.110");
        if (X == 0 || Ed == 0) throw new InvalidOperationException("系统加密库缺少 Ed25519/X25519");
    }
    static IntPtr Key(int type, byte[] bytes, bool secret)
    {
        IntPtr result = secret ? EVP_PKEY_new_raw_private_key(type, Zero, bytes, (ulong)bytes.Length)
            : EVP_PKEY_new_raw_public_key(type, Zero, bytes, (ulong)bytes.Length);
        if (result == Zero) throw new CryptographicException();
        return result;
    }
    static void Check(int v) { if (v != 1) throw new CryptographicException(); }
    public static byte[] Random()
    {
        byte[] b = new byte[32];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
        return b;
    }
    public static byte[] LoadIdentity()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"iScreenX"));
        string path = Path.Combine(dir.FullName,"identity");
        if (!File.Exists(path)) {
            byte[] seed = Random();
            string temp = Path.Combine(dir.FullName, Guid.NewGuid().ToString());
            try {
                File.WriteAllBytes(temp, ProtectedData.Protect(seed,null,DataProtectionScope.CurrentUser));
                try { File.Move(temp,path); } catch (IOException) { if (!File.Exists(path)) throw; }
            } finally { File.Delete(temp); Array.Clear(seed,0,seed.Length); }
        }
        return ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser);
    }
    public static byte[] Public(int type, byte[] seed)
    {
        IntPtr key = Key(type,seed,true);
        try { byte[] b = new byte[32]; ulong n = 32; Check(EVP_PKEY_get_raw_public_key(key,b,ref n)); return b; }
        finally { EVP_PKEY_free(key); }
    }
    public static byte[] Shared(byte[] seed, byte[] peer)
    {
        IntPtr key = Key(X,seed,true), pub = Zero, ctx = Zero;
        try {
            pub = Key(X,peer,false); ctx = EVP_PKEY_CTX_new(key,Zero);
            if (ctx == Zero) throw new CryptographicException();
            Check(EVP_PKEY_derive_init(ctx)); Check(EVP_PKEY_derive_set_peer(ctx,pub));
            byte[] b = new byte[32]; ulong n = 32; Check(EVP_PKEY_derive(ctx,b,ref n)); return b;
        } finally { EVP_PKEY_CTX_free(ctx); EVP_PKEY_free(pub); EVP_PKEY_free(key); }
    }
    public static byte[] Signature(byte[] keyBytes, byte[] msg, byte[] verify)
    {
        IntPtr key = Key(Ed,keyBytes,verify == null), ctx = EVP_MD_CTX_new();
        try {
            if (ctx == Zero) throw new CryptographicException();
            if (verify == null) {
                Check(EVP_DigestSignInit(ctx,Zero,Zero,Zero,key)); byte[] sig = new byte[64]; ulong n = 64;
                Check(EVP_DigestSign(ctx,sig,ref n,msg,(ulong)msg.Length)); return sig;
            }
            Check(EVP_DigestVerifyInit(ctx,Zero,Zero,Zero,key));
            Check(EVP_DigestVerify(ctx,verify,(ulong)verify.Length,msg,(ulong)msg.Length)); return verify;
        } finally { EVP_MD_CTX_free(ctx); EVP_PKEY_free(key); }
    }
}

internal sealed class PairSession
{
    byte[] shared, local, remote, peer;
    public byte[] VerifiedSecret { get; private set; }
    static byte[] Derive(string label, byte[] secret)
    {
        using (var sha = SHA512.Create())
            return sha.ComputeHash(Encoding.ASCII.GetBytes(label).Concat(secret).ToArray()).Take(16).ToArray();
    }
    byte[] Transform(byte[] message, int precedingBlocks)
    {
        using (var aes = Aes.Create()) {
            aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
            aes.Key = Derive("Pair-Verify-AES-Key", shared);
            byte[] counter = Derive("Pair-Verify-AES-IV", shared), block = new byte[16], result = new byte[message.Length];
            using (var cipher = aes.CreateEncryptor()) {
                for (int i = -precedingBlocks*16; i < message.Length; i += 16) {
                    cipher.TransformBlock(counter,0,16,block,0);
                    if (i >= 0) for (int j=0;j<16;j++) result[i+j]=(byte)(block[j]^message[i+j]);
                    for (int j=15;j>=0;j--) if (++counter[j] != 0) break;
                }
            }
            return result;
        }
    }
    public void Clear()
    {
        if (VerifiedSecret != null) Array.Clear(VerifiedSecret,0,VerifiedSecret.Length);
        VerifiedSecret = null;
        if (shared != null) Array.Clear(shared,0,shared.Length);
        shared = local = remote = peer = null;
    }
    public byte[] Verify(byte[] body, byte[] identity)
    {
        if (body.Length != 68) throw new CryptographicException();
        if (body[0] == 1) {
            Clear();
            remote = body.Skip(4).Take(32).ToArray(); peer = body.Skip(36).ToArray();
            byte[] ephemeral = PairKeys.Random();
            try { local = PairKeys.Public(PairKeys.X,ephemeral); shared = PairKeys.Shared(ephemeral,remote); }
            finally { Array.Clear(ephemeral,0,32); }
            return local.Concat(Transform(PairKeys.Signature(identity,local.Concat(remote).ToArray(),null),0)).ToArray();
        }
        if (body[0] != 0 || shared == null) throw new CryptographicException();
        PairKeys.Signature(peer,remote.Concat(local).ToArray(),Transform(body.Skip(4).ToArray(),4));
        byte[] verified = (byte[])shared.Clone();
        Clear();
        VerifiedSecret = verified;
        return new byte[0];
    }
}
