using System;
using Microsoft.Win32;

internal static class StartupRegistration
{
    internal const string RunPath=@"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName="CPAplusQuotaAssistant";
    internal static string Command {get{return "\""+System.Windows.Forms.Application.ExecutablePath+"\" --background";}}
    internal static bool Enabled
    {
        get {using(var key=Registry.CurrentUser.OpenSubKey(RunPath))return key!=null&&string.Equals(key.GetValue(ValueName) as string,Command,StringComparison.OrdinalIgnoreCase);}
    }
    internal static void SetEnabled(bool value)
    {
        using(var key=Registry.CurrentUser.CreateSubKey(RunPath)){
            if(value)key.SetValue(ValueName,Command,RegistryValueKind.String);
            else key.DeleteValue(ValueName,false);
        }
    }
}
