using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

// Test-only transport; requests never leave the process and use fictitious credentials.
internal sealed class PendingQuotaHandler : HttpMessageHandler
{
    internal sealed class Pending
    {
        internal string Method,Path,Alias;
        internal readonly TaskCompletionSource<HttpResponseMessage> Completion=new TaskCompletionSource<HttpResponseMessage>();
    }
    internal readonly List<Pending> Requests=new List<Pending>();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
    {
        if(request.RequestUri.Host!="quota.example.invalid"||request.Headers.Authorization.Parameter!="fake-interaction-key")throw new Exception("Test boundary");
        IEnumerable<string> aliases;
        var pending=new Pending {Method=request.Method.Method,Path=request.RequestUri.AbsolutePath,Alias=request.Headers.TryGetValues("X-Quota-Account",out aliases)?string.Join("",aliases):null};
        Requests.Add(pending);
        using(cancellation.Register(()=>pending.Completion.TrySetCanceled()))return await pending.Completion.Task.ConfigureAwait(false);
    }
    internal void Reply(int index,QuotaResult data,HttpStatusCode status=HttpStatusCode.OK)
    {
        Requests[index].Completion.SetResult(new HttpResponseMessage(status){Content=new StringContent(new JavaScriptSerializer().Serialize(data))});
    }
}

internal static class InteractionProbe
{
    [DllImport("advapi32.dll")]private static extern int RegOverridePredefKey(IntPtr key,IntPtr value);
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool value,string label){if(!value)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
    private static void Set(object target,string name,object value){target.GetType().GetField(name,Flags).SetValue(target,value);}
    private static void Pump()
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};
        timer.Tick+=delegate {frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();
    }
    private static void Finish(Task task){for(int i=0;i<100&&!task.IsCompleted;i++)Pump();Check(task.IsCompleted,"controlled request completes without blocking UI");task.GetAwaiter().GetResult();}
    private static QuotaResult Data(string mode="auto",string alias=null)
    {
        return new QuotaResult {selection=new AccountSelection {mode=mode,alias=alias},accounts=new List<QuotaAccount>{
            new QuotaAccount {alias="fake-a",displayName="Primary",availabilityKnown=true,available=true,supported=true,cacheStatus="ok",lastSuccessAt="2026-10-23T12:00:00+08:00",weekly=new QuotaWindow {remainingPercent=61}},
            new QuotaAccount {alias="fake-b",displayName="Backup",availabilityKnown=true,available=true,supported=true,cacheStatus="ok",lastSuccessAt="2026-10-23T12:00:00+08:00",weekly=new QuotaWindow {remainingPercent=42}}
        }};
    }
    [STAThread]private static int Main()
    {
        string isolated=@"Software\CPAplusQuotaInteractionTests\"+Guid.NewGuid().ToString("N");RegistryKey hive=null;
        string noteFile=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","evidence","interaction-notes-"+Guid.NewGuid().ToString("N")+".json");
        QuotaForm form=null;TaskbarStrip strip=null;QuotaApi api=null;System.Windows.Forms.NotifyIcon tray=null;System.Windows.Forms.Timer timer=null;
        try {
            hive=Registry.CurrentUser.CreateSubKey(isolated);
            if(RegOverridePredefKey(new IntPtr(unchecked((int)0x80000001)),hive.Handle.DangerousGetHandle())!=0)throw new Exception("Registry isolation failed");
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var handler=new PendingQuotaHandler();api=new QuotaApi(handler,"https://quota.example.invalid");
            form=new QuotaForm();typeof(QuotaForm).GetMethod("EnsurePopup",Flags).Invoke(form,null);
            var popup=(QuotaPopup)typeof(QuotaForm).GetField("popup",Flags).GetValue(form);
            var root=(FrameworkElement)typeof(QuotaPopup).GetField("root",Flags).GetValue(popup);var refresh=(Button)root.FindName("RefreshButton");
            strip=new TaskbarStrip();tray=new System.Windows.Forms.NotifyIcon();timer=new System.Windows.Forms.Timer();
            // Bypass startup only: this must never load the operator's Credential Manager or register hooks.
            var context=(TrayContext)FormatterServices.GetUninitializedObject(typeof(TrayContext));
            Set(context,"form",form);Set(context,"strip",strip);Set(context,"api",api);Set(context,"tray",tray);Set(context,"timer",timer);Set(context,"active",true);Set(context,"key","fake-interaction-key");
            var initial=Data();strip.UpdateData(initial,null);form.ShowAccounts(initial,"fake-a");
            Task manual=null,selection=null;
            form.RefreshRequested+=delegate {manual=context.Refresh(true);};
            form.PreferAccountRequested+=delegate(string alias){selection=(Task)typeof(TrayContext).GetMethod("SelectPreference",Flags).Invoke(context,new object[]{alias});};
            var background=context.Refresh();Check(handler.Requests.Count==1&&refresh.IsEnabled,"background read leaves refresh usable immediately");
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(handler.Requests.Count==2&&handler.Requests[0].Completion.Task.IsCanceled,"explicit refresh cancels and replaces only the background read");
            Check(handler.Requests[1].Method=="POST"&&handler.Requests[1].Alias=="fake-a"&&!refresh.IsEnabled,"button sends one real API operation for displayed account and disables repeats");
            var duplicate=context.Refresh(true);Check(handler.Requests.Count==2&&duplicate.IsCompleted,"repeated refresh does not duplicate an in-flight user operation");
            var updated=Data();updated.refreshStatus="success";updated.accounts[0].weekly.remainingPercent=60;updated.accounts[0].lastSuccessAt="2026-10-23T12:00:30+08:00";
            handler.Reply(1,updated);Finish(manual);Finish(background);
            Check(strip.CurrentData.accounts[0].weekly.remainingPercent==60&&refresh.IsEnabled,"confirmed refresh updates quota and re-enables button");
            Check(((TextBlock)root.FindName("FailureText")).Text=="额度已更新。","confirmed upstream refresh has explicit completion feedback");
            background=context.Refresh();handler.Reply(2,null,HttpStatusCode.BadGateway);Finish(background);
            Check(strip.CurrentData!=null&&strip.CurrentData.accounts[0].weekly.remainingPercent==60&&strip.CurrentData.accounts[0].lastSuccessAt==updated.accounts[0].lastSuccessAt,"network failure retains last confirmed quota and genuine success time");
            Check(((TextBlock)root.FindName("FailureText")).Text.Contains("上次结果")&&((TextBlock)root.FindName("FailureText")).Visibility==Visibility.Visible,"network failure is explicit rather than an empty account screen");
            background=context.Refresh();var others=(Expander)root.FindName("OtherAccounts");others.IsExpanded=true;
            var row=(Expander)((StackPanel)root.FindName("AccountRows")).Children[0];row.IsExpanded=true;
            var action=((StackPanel)row.Content).Children[1] as Button;action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(handler.Requests.Count==5&&handler.Requests[3].Completion.Task.IsCanceled&&handler.Requests[4].Method=="PUT","explicit account selection preempts background read");
            Check(strip.SelectedAlias=="fake-a"&&others.IsExpanded&&object.Equals(action.Content,"切换中…"),"selection waits for server confirmation and keeps browsing context");
            handler.Reply(4,Data("manual","fake-b"));Finish(selection);Finish(background);
            Check(strip.SelectedAlias=="fake-b"&&((TextBlock)root.FindName("AccountName")).Text=="Backup"&&others.IsExpanded,"acknowledged selection updates popup and taskbar together without collapsing list");
            background=context.Refresh();handler.Reply(5,Data("manual","fake-b"));Finish(background);
            Check(((TextBlock)root.FindName("FailureText")).Visibility!=Visibility.Visible,"connection recovery clears transient network error");
            selection=(Task)typeof(TrayContext).GetMethod("SelectPreference",Flags).Invoke(context,new object[]{"fake-a"});handler.Reply(handler.Requests.Count-1,null,HttpStatusCode.BadGateway);Finish(selection);
            Check(strip.SelectedAlias=="fake-b"&&((TextBlock)root.FindName("FailureText")).Text.Contains("未确认")&&refresh.IsEnabled,"failed selection retains confirmed account and allows retry");
            background=context.Refresh();handler.Requests[handler.Requests.Count-1].Completion.TrySetCanceled();Finish(background);
            Check(strip.CurrentData!=null&&((TextBlock)root.FindName("FailureText")).Text.Contains("超时"),"read timeout retains data and communicates failure");
            background=context.Refresh();handler.Reply(handler.Requests.Count-1,null,HttpStatusCode.Unauthorized);Finish(background);
            Check(strip.CurrentData==null&&((StackPanel)root.FindName("AccountRows")).Children.Count==0,"revoked credentials still immediately clear all retained account data");
            strip.UpdateData(initial,null);form.ShowAccounts(initial,"fake-a");
            background=context.Refresh();handler.Reply(handler.Requests.Count-1,null,HttpStatusCode.Forbidden);Finish(background);
            Check(strip.CurrentData==null,"unbound credentials clear retained data just like revoked credentials");
            AccountNames.PathName=noteFile;form.ShowAccounts(initial,"fake-a");
            typeof(QuotaPopup).GetMethod("Settings",Flags).Invoke(popup,new object[]{true});
            ((TextBox)root.FindName("NoteInput")).Text="Local primary note";form.SetPrimaryAlias("fake-b");
            ((Button)root.FindName("SaveNoteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(AccountNames.Note("fake-a")=="Local primary note"&&AccountNames.Note("fake-b")==null,"note save remains attached to edited account when main display changes in background");
            popup.SetReadFeedback("正在切换调用账号…");Check(((TextBlock)root.FindName("SettingsNotice")).Text.Contains("正在切换"),"settings actions report progress on the visible page");
            var narrowed=Data();narrowed.accounts.RemoveAt(0);form.ShowAccounts(narrowed,"fake-b");
            Check(!((Button)root.FindName("SaveNoteButton")).IsEnabled,"removed account cannot receive a note from an obsolete editing context");
            Check(!popup.IsVisible&&!strip.Visible,"interaction verification never focuses or overlaps the user's running helper");
            Console.WriteLine("TOTAL "+checks+" checks PASS; in-process mock HTTP only, no credential read or upstream access");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally {
            if(form!=null)form.Dispose();if(strip!=null)strip.Dispose();if(api!=null)api.Dispose();if(tray!=null)tray.Dispose();if(timer!=null)timer.Dispose();
            RegOverridePredefKey(new IntPtr(unchecked((int)0x80000001)),IntPtr.Zero);if(hive!=null)hive.Dispose();Registry.CurrentUser.DeleteSubKeyTree(isolated,false);
            if(File.Exists(noteFile))File.Delete(noteFile);
        }
    }
}
