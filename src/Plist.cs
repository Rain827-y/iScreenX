using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// Binary property lists used by the AirPlay control connection.
internal sealed class Plist
{
    readonly byte[] data;
    readonly int offsets, offsetSize, refSize, count;
    int budget = 8192;
    Plist(byte[] bytes)
    {
        data = bytes;
        int t = data.Length - 32;
        if (t < 8 || Encoding.ASCII.GetString(data,0,8) != "bplist00") throw new InvalidDataException();
        offsetSize = data[t+6]; refSize = data[t+7];
        count = checked((int)Number(t+8,8)); offsets = checked((int)Number(t+24,8));
        if (count < 1 || count > 8192 || offsetSize < 1 || offsetSize > 8 || refSize < 1 || refSize > 8 ||
            offsets < 8 || (long)offsets + count*offsetSize > t) throw new InvalidDataException();
    }
    ulong Number(int p,int n)
    {
        if (p < 0 || n > 8 || n < 1 || p > data.Length-n) throw new InvalidDataException();
        ulong v=0; for(int i=0;i<n;i++) v=(v<<8)|data[p+i]; return v;
    }
    object Object(int id,int depth)
    {
        if(id<0 || id>=count || depth>32 || --budget<0) throw new InvalidDataException();
        int p=checked((int)Number(offsets+id*offsetSize,offsetSize));
        if(p<8 || p>=offsets) throw new InvalidDataException();
        int tag=data[p++], kind=tag>>4, n=tag&15;
        if(kind==0) return n==9;
        if(kind==1) return Number(p,1<<n);
        if(kind==2 || kind==3 || kind==8) return null;
        if(n==15) {
            int marker=data[p++];
            if(marker<16 || marker>19) throw new InvalidDataException();
            int size=1<<(marker&15); n=checked((int)Number(p,size)); p+=size;
        }
        long width = kind==6 ? 2 : kind==10 ? refSize : kind==13 ? refSize*2 : 1;
        if(n<0 || p+(long)n*width>offsets) throw new InvalidDataException();
        if(kind==4) {byte[] b=new byte[n]; Buffer.BlockCopy(data,p,b,0,n);return b;}
        if(kind==5 || kind==6) return (kind==5?Encoding.ASCII:Encoding.BigEndianUnicode).GetString(data,p,(int)(n*width));
        if(n>8192) throw new InvalidDataException();
        if(kind==10) {
            object[] a=new object[n]; for(int i=0;i<n;i++) a[i]=Object(checked((int)Number(p+i*refSize,refSize)),depth+1);return a;
        }
        if(kind==13) {
            var d=new Dictionary<string,object>();
            for(int i=0;i<n;i++) {
                string k=Object(checked((int)Number(p+i*refSize,refSize)),depth+1) as string;
                if(k==null) throw new InvalidDataException();
                d[k]=Object(checked((int)Number(p+(i+n)*refSize,refSize)),depth+1);
            }
            return d;
        }
        throw new InvalidDataException();
    }
    public static Dictionary<string,object> Read(byte[] bytes)
    {
        var p=new Plist(bytes);
        var d=p.Object(checked((int)p.Number(bytes.Length-16,8)),0) as Dictionary<string,object>;
        if(d==null) throw new InvalidDataException(); return d;
    }
    static void UInt(Stream s,ulong n,int size) { for(int i=size-1;i>=0;i--)s.WriteByte((byte)(n>>(i*8))); }
    static void Tag(Stream s,int type,int n) {
        s.WriteByte((byte)(type+Math.Min(15,n)));
        if(n>=15){s.WriteByte(0x12);UInt(s,(uint)n,4);}
    }
    static int Add(List<byte[]> objects,object value)
    {
        int id=objects.Count; objects.Add(null); var s=new MemoryStream();
        var dict=value as Dictionary<string,object>; var list=value as object[];
        if(dict!=null) {
            Tag(s,0xd0,dict.Count);
            foreach(string k in dict.Keys) UInt(s,(uint)Add(objects,k),2);
            foreach(object v in dict.Values) UInt(s,(uint)Add(objects,v),2);
        } else if(list!=null) {
            Tag(s,0xa0,list.Length);foreach(object v in list)UInt(s,(uint)Add(objects,v),2);
        } else if(value is byte[] || value is string) {
            byte[] bytes=value as byte[] ?? Encoding.ASCII.GetBytes((string)value);
            Tag(s,value is string?0x50:0x40,bytes.Length);s.Write(bytes,0,bytes.Length);
        } else if(value is bool) s.WriteByte((byte)((bool)value?9:8));
        else if(value is double) {s.WriteByte(0x23);UInt(s,(ulong)BitConverter.DoubleToInt64Bits((double)value),8);}
        else {s.WriteByte(0x13);UInt(s,Convert.ToUInt64(value),8);}
        objects[id]=s.ToArray();return id;
    }
    public static byte[] Write(Dictionary<string,object> d)
    {
        var all=new List<byte[]>();Add(all,d);var s=new MemoryStream();byte[] magic=Encoding.ASCII.GetBytes("bplist00");
        s.Write(magic,0,8);var indexes=new List<long>();
        foreach(byte[] o in all){indexes.Add(s.Position);s.Write(o,0,o.Length);}
        long pos=s.Position;foreach(long p in indexes)UInt(s,(ulong)p,4);
        UInt(s,0,6);s.WriteByte(4);s.WriteByte(2);UInt(s,(uint)all.Count,8);UInt(s,0,8);UInt(s,(ulong)pos,8);
        return s.ToArray();
    }
}
