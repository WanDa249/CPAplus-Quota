using System;
using System.Drawing.Text;
using System.IO;
using System.Windows.Media;

internal static class UiFonts
{
    private static readonly string directory=EnsureBundledFonts();
    // Keep the private collection alive while GDI fonts reference its families.
    private static readonly PrivateFontCollection taskbarCollection=Load("Medium");
    private static PrivateFontCollection menuCollection;
    internal static readonly FontFamily Popup=new FontFamily(new Uri(directory+Path.DirectorySeparatorChar),"./#Source Han Sans CN");
    internal static System.Drawing.FontFamily Taskbar {get{return taskbarCollection.Families[0];}}
    internal static System.Drawing.FontFamily Menu {get{if(menuCollection==null)menuCollection=Load("Regular");return menuCollection.Families[0];}}
    private static string EnsureBundledFonts()
    {
        // WPF and GDI load the same original OTFs from a private user directory.
        // Publish complete files atomically so an interrupted first run can retry.
        string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CPAplusQuota","Fonts","source-han-sans-a4f7cf94edfb");
        Directory.CreateDirectory(folder);
        foreach(string file in new[]{"SourceHanSansCN-Regular.otf","SourceHanSansCN-Medium.otf","LICENSE.txt","THIRD_PARTY_NOTICES-v0.1.2-beta.txt"}){
            string destination=Path.Combine(folder,file);
            if(File.Exists(destination))continue;
            string temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                using(var input=typeof(UiFonts).Assembly.GetManifestResourceStream("BundledFonts."+file))
                using(var output=File.Create(temporary))input.CopyTo(output);
                File.Move(temporary,destination);
            } finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
        return folder;
    }
    private static PrivateFontCollection Load(string weight)
    {
        var fonts=new PrivateFontCollection();
        fonts.AddFontFile(Path.Combine(directory,"SourceHanSansCN-"+weight+".otf"));
        return fonts;
    }
}
