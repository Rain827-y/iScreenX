using System;
using System.Collections.Generic;

// AirPlay repeats recent AAC packets. Give a missing sequence number three
// successors to arrive before advancing the playback timeline.
internal sealed class AudioPacketQueue
{
    readonly Dictionary<ushort,byte[]> pending=new Dictionary<ushort,byte[]>();
    ushort next, newest;
    bool started;
    internal int Recovered, Missing;
    internal void Reset(){pending.Clear();started=false;}
    internal void Add(byte[] packet){
        ushort seq=(ushort)((packet[2]<<8)|packet[3]);
        if(!started){next=newest=seq;started=true;}
        int distance=(short)(seq-next);
        if(distance<0 || pending.ContainsKey(seq))return;
        if(distance>=64){Reset();next=newest=seq;started=true;}
        if((short)(seq-newest)>0)newest=seq;
        else if(seq!=newest)Recovered++;
        pending.Add(seq,packet);
    }
    internal bool Take(out byte[] packet){
        packet=null;
        if(!started)return false;
        if(pending.TryGetValue(next,out packet))pending.Remove(next);
        else if((short)(newest-next)>=3)Missing++;
        else return false;
        next=unchecked((ushort)(next+1));return true;
    }
}
