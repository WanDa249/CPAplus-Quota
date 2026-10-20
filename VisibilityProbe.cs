using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

internal static class VisibilityProbe
{
    private const BindingFlags Flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static Assembly app;
    private static int count;
    [DllImport("advapi32.dll")] private static extern int RegOverridePredefKey(IntPtr key,IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private static Type T(string name){return app.GetType(name,true);}
    private static object Mode(string name){return Enum.Parse(T("VisibilityMode"),name);}
    private static object Call(string type,string method,object instance,params object[] args){return T(type).GetMethod(method,Flags).Invoke(instance,args);}
    private static void Require(bool yes,string label){if(!yes)throw new Exception(label);count++;Console.WriteLine("PASS "+label);}
    private static IList Windows(params bool[] minimized)
    {
        var list=(IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(T("DesktopFacts+CodexWindow")));
        foreach(bool value in minimized){var window=Activator.CreateInstance(T("DesktopFacts+CodexWindow"),true);T("DesktopFacts+CodexWindow").GetField("minimized").SetValue(window,value);list.Add(window);}
        return list;
    }
    private static string Reason(string mode,bool foreground,params bool[] minimized){return (string)Call("DesktopMonitor","VisibilityReason",null,Mode(mode),Windows(minimized),foreground);}

    [STAThread] private static int Main()
    {
        string registry=@"Software\CPAplusQuotaVisibilityTests\"+Guid.NewGuid().ToString("N");
        object popup=null,strip=null;RegistryKey hive=null;
        try {
            hive=Registry.CurrentUser.CreateSubKey(registry);
            Require(RegOverridePredefKey(new IntPtr(unchecked((int)0x80000001)),hive.Handle.DangerousGetHandle())==0,"settings isolated from current user");
            app=Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CPAplusQuota.exe"));
            Require(Reason("CodexRunning",false)=="codex-closed","closed Codex hides default mode");
            Require(Reason("CodexRunning",false,true)==null,"existing default still shows minimized Codex");
            Require(Reason("CodexNotMinimized",false)=="codex-closed","new mode hides absent Codex");
            Require(Reason("CodexNotMinimized",false,true)=="codex-minimized","new mode hides one minimized window");
            Require(Reason("CodexNotMinimized",false,true,true)=="codex-minimized","new mode hides when all windows minimized");
            Require(Reason("CodexNotMinimized",false,true,false)==null,"one restored window shows even when another is minimized");
            Require(Reason("CodexNotMinimized",false,false)==null,"switching foreground app alone does not hide new mode");
            Require(Reason("CodexNotMinimized",true,false)==null,"restored foreground Codex shows");
            Require(Reason("CodexForeground",false,false)=="codex-not-foreground","old foreground-only policy retained");
            Require(Reason("CodexForeground",true,false)==null,"old foreground mode shows foreground Codex");
            Require(Reason("Always",false)==null&&Reason("Always",false,true,true)==null,"always mode independent of process/minimize status");
            Require(Reason("Hidden",false)=="user-hidden"&&Reason("Hidden",true,false)=="user-hidden","manual hidden overrides Codex presence and foreground");
            Require((int)Call("DesktopMonitor","LoadMode",null)==0,"no saved choice preserves original default");
            using(var key=Registry.CurrentUser.CreateSubKey(@"Software\CPAplusQuota"))key.SetValue("ServerUrl","https://quota.example.invalid");
            var monitor=FormatterServices.GetUninitializedObject(T("DesktopMonitor"));
            var property=T("DesktopMonitor").GetProperty("Mode",Flags);
            property.SetValue(monitor,Mode("CodexNotMinimized"),null);
            Require((int)Call("DesktopMonitor","LoadMode",null)==3,"minimize policy persisted for next startup");
            property.SetValue(monitor,Mode("Hidden"),null);
            Require((int)Call("DesktopMonitor","LoadMode",null)==4,"hidden choice persisted for next startup");
            bool rejected=false;try{property.SetValue(monitor,Enum.ToObject(T("VisibilityMode"),99),null);}catch(TargetInvocationException error){rejected=error.InnerException is ArgumentOutOfRangeException;}
            Require(rejected&&(int)Call("DesktopMonitor","LoadMode",null)==4,"invalid mode cannot overwrite saved choice");
            using(var key=Registry.CurrentUser.OpenSubKey(@"Software\CPAplusQuota"))Require((string)key.GetValue("ServerUrl")=="https://quota.example.invalid","saving display mode preserves server configuration");
            popup=Activator.CreateInstance(T("QuotaPopup"),true);var root=(FrameworkElement)((Window)popup).Content;
            var combo=(ComboBox)root.FindName("DisplayMode");
            Require(combo.Items.Count==5&&combo.Items[3].ToString()=="Codex 未最小化时显示"&&combo.Items[4].ToString()=="隐藏任务栏额度","actual settings list contains both choices");
            int selected=-1;
            T("QuotaPopup").GetEvent("ModeRequested",Flags).GetAddMethod(true).Invoke(popup,new object[]{new Action<int>(value=>{selected=value;property.SetValue(monitor,Enum.ToObject(T("VisibilityMode"),value),null);})});
            combo.SelectedIndex=3;Require(selected==3&&(int)Call("DesktopMonitor","LoadMode",null)==3,"actual combo selection saves minimize mode");
            combo.SelectedIndex=4;Require(selected==4&&(int)Call("DesktopMonitor","LoadMode",null)==4,"actual combo selection saves hidden mode");
            Require(!((Window)popup).IsVisible,"test does not show or focus a window");
            var refresh=(Button)root.FindName("RefreshButton");
            Call("QuotaPopup","SetBusy",popup,true);
            Require((string)refresh.Content=="查询中…"&&!refresh.IsEnabled,"query progress uses concise copy and prevents duplicate clicks");
            Call("QuotaPopup","SetBusy",popup,false);
            Require((string)refresh.Content=="刷新"&&refresh.IsEnabled,"query button returns to refresh after completion");
            Func<string,string,bool,bool,bool,bool> animate=(before,alias,visible,usable,motion)=>(bool)Call("TaskbarStrip","AnimateChange",null,before,"5h 61% · 周 46%","account-a",alias,visible,usable,motion);
            Require(animate("5h 62% · 周 46%","account-a",true,true,true),"changed quota for same account animates");
            Require(!animate("5h 61% · 周 46%","account-a",true,true,true),"unchanged 15-second reads do not replay animation");
            Require(!animate(null,"account-a",true,true,true),"initial data does not animate");
            Require(!animate("old","account-b",true,true,true),"account switching does not animate");
            Require(!animate("old","account-a",false,true,true),"show or restore does not animate");
            Require(!animate("old","account-a",true,false,true),"unusable or failed quota does not animate");
            Require(!animate("old","account-a",true,true,false),"disabled Windows animations respected");
            byte previous=0;bool monotonic=true;
            for(int ms=0;ms<=300;ms+=25){byte alpha=(byte)Call("TaskbarStrip","TransitionAlpha",null,(double)ms);monotonic&=alpha>=previous;previous=alpha;}
            Require(monotonic&&(byte)Call("TaskbarStrip","TransitionAlpha",null,-100d)==208&&(byte)Call("TaskbarStrip","TransitionAlpha",null,200d)==255,"fade is monotonic, restrained and completes within 200ms");
            strip=Activator.CreateInstance(T("TaskbarStrip"),true);
            var transition=(System.Windows.Forms.Timer)T("TaskbarStrip").GetField("transition",Flags).GetValue(strip);
            Require(!transition.Enabled&&!((System.Windows.Forms.Form)strip).Visible,"real strip constructs without showing or running animation timer");
            T("TaskbarStrip").GetField("transitionFrame",Flags).SetValue(strip,new System.Drawing.Bitmap(2,2));transition.Start();
            Call("TaskbarStrip","OnVisibleChanged",strip,EventArgs.Empty);
            Require(!transition.Enabled&&T("TaskbarStrip").GetField("transitionFrame",Flags).GetValue(strip)==null,"hiding stops timer and releases animation bitmap");
            var result=Activator.CreateInstance(T("QuotaResult"),true);
            T("QuotaResult").GetProperty("refreshStatus").SetValue(result,"cached",null);
            Require((string)Call("AccountPresentation","RefreshFeedback",null,result)=="","reused result has no technical cache banner or false upstream success");
            T("QuotaResult").GetProperty("refreshStatus").SetValue(result,"error",null);
            Require(((string)Call("AccountPresentation","RefreshFeedback",null,result)).Contains("失败"),"query failure remains explicit");
            var account=Activator.CreateInstance(T("QuotaAccount"),true);
            T("QuotaAccount").GetProperty("supported").SetValue(account,true,null);T("QuotaAccount").GetProperty("stale").SetValue(account,true,null);
            Require(((string)Call("AccountPresentation","CacheState",null,account)).Contains("已过期"),"old data remains visibly expired");
            Require((string)Call("AccountPresentation","Updated",null,"2020-01-02T03:04:00+08:00")=="额度更新于 1月2日 03:04","success time remains actual supplied time rather than current query time");
            var accountList=(IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(T("QuotaAccount")));accountList.Add(account);
            T("QuotaResult").GetProperty("accounts").SetValue(result,accountList,null);
            foreach(string flag in new[]{"available","availabilityKnown"})T("QuotaAccount").GetProperty(flag).SetValue(account,true,null);
            T("QuotaAccount").GetProperty("alias").SetValue(account,"test-account",null);
            var five=Activator.CreateInstance(T("QuotaWindow"),true);T("QuotaWindow").GetProperty("remainingPercent").SetValue(five,62d,null);T("QuotaAccount").GetProperty("fiveHour").SetValue(account,five,null);
            IntPtr foreground=GetForegroundWindow();
            Call("TaskbarStrip","UpdateData",strip,result,null);
            Call("TaskbarStrip","Present",strip,new System.Drawing.Rectangle(-32000,-32000,1000,48),-32000,-31000,96);
            Require(!transition.Enabled,"first real offscreen render does not animate");
            T("QuotaWindow").GetProperty("remainingPercent").SetValue(five,61d,null);Call("TaskbarStrip","UpdateData",strip,result,null);
            Call("TaskbarStrip","Present",strip,new System.Drawing.Rectangle(-32000,-32000,1000,48),-32000,-31000,96);
            Require(transition.Enabled==SystemParameters.ClientAreaAnimation,"real changed-value render follows system animation preference");
            var elapsed=System.Diagnostics.Stopwatch.StartNew();while(elapsed.ElapsedMilliseconds<300){System.Windows.Forms.Application.DoEvents();System.Threading.Thread.Sleep(10);}
            Require(!transition.Enabled&&T("TaskbarStrip").GetField("transitionFrame",Flags).GetValue(strip)==null,"real layered-window animation finishes and releases its frame");
            Require(GetForegroundWindow()==foreground,"real strip render and animation do not take focus");
            ((System.Windows.Forms.Form)strip).Hide();
            using(var key=Registry.CurrentUser.CreateSubKey(@"Software\CPAplusQuota"))key.SetValue("DisplayMode",99);
            Require((int)Call("DesktopMonitor","LoadMode",null)==0,"unknown saved choice falls back to original default");
            Console.WriteLine("TOTAL "+count+" checks PASS; no credentials read, no HTTP requests");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally {
            if(strip!=null)((IDisposable)strip).Dispose();
            if(popup!=null)Call("QuotaPopup","Shutdown",popup);
            RegOverridePredefKey(new IntPtr(unchecked((int)0x80000001)),IntPtr.Zero);
            if(hive!=null)hive.Dispose();Registry.CurrentUser.DeleteSubKeyTree(registry,false);
        }
    }
}
