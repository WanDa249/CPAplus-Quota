using System;
using Microsoft.Win32;

internal static class ServiceEndpoint
{
    private const string SettingsPath=@"Software\CPAplusQuota";
    internal static string Current=Load();
    internal static string Normalize(string value)
    {
        Uri uri;
        if(!Uri.TryCreate((value??"").Trim(),UriKind.Absolute,out uri)||uri.Scheme!=Uri.UriSchemeHttps||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0||uri.AbsolutePath!="/")
            throw new ArgumentException("HTTPS origin required.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    private static string Load()
    {
        using(var key=Registry.CurrentUser.OpenSubKey(SettingsPath)){
            try {return key==null?null:Normalize(key.GetValue("ServerUrl") as string);}
            catch(ArgumentException){return null;}
        }
    }
    internal static void Save(string value)
    {
        value=Normalize(value);
        using(var key=Registry.CurrentUser.CreateSubKey(SettingsPath))key.SetValue("ServerUrl",value,RegistryValueKind.String);
        Current=value;
    }
}
