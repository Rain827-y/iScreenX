using System;
using Microsoft.Win32;

internal sealed class DisplayMode
{
    const string SettingsKey=@"HKEY_CURRENT_USER\Software\iScreenX";
    internal readonly int Height, Fps;
    internal int Width {get{return Height==1080?1920:1280;}}
    DisplayMode(int height,int fps){Height=height;Fps=fps;}

    internal static DisplayMode Parse(string value){
        switch(value){
            case "720/30": return new DisplayMode(720,30);
            case "720/60": return new DisplayMode(720,60);
            case "1080/30": return new DisplayMode(1080,30);
            case "1080/60": return new DisplayMode(1080,60);
            default: return null;
        }
    }
    internal static DisplayMode Load(){
        return Parse(Registry.GetValue(SettingsKey,"DisplayMode",null) as string) ?? new DisplayMode(720,30);
    }
    internal void Save(){Registry.SetValue(SettingsKey,"DisplayMode",ToString());}
    public override string ToString(){return Height+"/"+Fps;}
}
