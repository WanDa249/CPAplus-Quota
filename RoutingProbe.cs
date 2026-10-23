using System;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// Test-only bridge: shipped QuotaApi still requires HTTPS and never accepts loopback overrides.
internal sealed class LocalRoutingHandler : HttpMessageHandler
{
    private readonly string backend;
    private readonly HttpClient local = new HttpClient(new HttpClientHandler {AllowAutoRedirect=false,UseProxy=false});
    internal int Selections;
    internal LocalRoutingHandler(string address) {backend=address;}
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
    {
        if(request.RequestUri.Host!="quota.example.invalid"||request.RequestUri.Query.Length!=0||request.Headers.Authorization.Parameter!="fake-bound")throw new Exception("Test credential boundary");
        if(request.Method==HttpMethod.Put)Selections++;
        using(var forwarded=new HttpRequestMessage(request.Method,backend+request.RequestUri.AbsolutePath)) {
            foreach(var header in request.Headers)forwarded.Headers.TryAddWithoutValidation(header.Key,header.Value);
            return await local.SendAsync(forwarded,cancellation).ConfigureAwait(false);
        }
    }
    protected override void Dispose(bool disposing){if(disposing)local.Dispose();base.Dispose(disposing);}
}

internal static class RoutingProbe
{
    [DllImport("advapi32.dll")]private static extern int RegOverridePredefKey(IntPtr key,IntPtr value);
    private static int checks;
    private static void Check(bool yes,string label){if(!yes)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
    private static Button Choice(FrameworkElement root){return ((StackPanel)((Expander)((StackPanel)root.FindName("AccountRows")).Children[0]).Content).Children.OfType<Button>().Single();}
    private static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
    private static T Wait<T>(Func<Task<T>> action){return Task.Run(action).GetAwaiter().GetResult();}
    [STAThread]private static int Main(string[] args)
    {
        string isolated=@"Software\CPAplusQuotaRoutingTests\"+Guid.NewGuid().ToString("N");
        RegistryKey hive=null;QuotaPopup popup=null;TaskbarStrip strip=null;
        try {
            hive=Registry.CurrentUser.CreateSubKey(isolated);
            if(RegOverridePredefKey(new IntPtr(unchecked((int)0x80000001)),hive.Handle.DangerousGetHandle())!=0)throw new Exception("Registry isolation failed");
            using(var settings=Registry.CurrentUser.CreateSubKey(@"Software\CPAplusQuota"))settings.SetValue("ServerUrl","https://quota.example.invalid");
            var handler=new LocalRoutingHandler(args[0]);
            using(var api=new QuotaApi(handler,"https://quota.example.invalid")) {
                var data=Wait(()=>api.SelectAccountAsync("fake-bound",null,CancellationToken.None));
                data=Wait(()=>api.ReadAsync("fake-bound",CancellationToken.None));
                Check(data.selection.mode=="auto"&&data.accounts.Count==2,"actual Go API returns own group and automatic selection");
                Check(data.accounts[0].displayName=="b.json@example.invalid","API list follows descending priority");
                int start=handler.Selections;
                popup=new QuotaPopup();strip=new TaskbarStrip();
                var root=(FrameworkElement)typeof(QuotaPopup).GetField("root",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(popup);
                int events=0,display=0;string selected=null;
                popup.PreferAccountRequested+=delegate(string alias){events++;selected=alias;};
                popup.AccountSelected+=delegate(string alias){display++;};
                popup.SetData(data);popup.SetPrimary(data.accounts[0].alias);strip.UpdateData(data,null);strip.SelectDefaultAccount();
                Check(handler.Selections==start&&events==0,"startup, render and automatic display send no preference writes");
                Check((string)Choice(root).Content=="优先使用并主显示","explicit account action states routing effect");
                Click(Choice(root));
                Check(events==1&&selected==data.accounts[1].alias&&display==0,"explicit other-account click requests preference only");
                data=Wait(()=>api.SelectAccountAsync("fake-bound",selected,CancellationToken.None));
                Check(data.selection.mode=="manual"&&data.selection.alias==selected,"real transport and Go API acknowledge exact selected account");
                popup.SetData(data);popup.SetPrimary(selected);strip.UpdateData(data,null);strip.SelectAccount(selected);
                Check(strip.SelectedAlias==selected,"taskbar main display follows confirmed preference");
                Check(((Button)root.FindName("AutoRouteButton")).IsEnabled,"automatic-order reset available in settings");
                popup.SetBusy(true);
                Check(!Choice(root).IsEnabled&&!((Button)root.FindName("UsePrimaryButton")).IsEnabled,"in-flight request disables duplicate selection buttons");
                popup.SetBusy(false);
                Click((Button)root.FindName("AutoRouteButton"));
                Check(events==2&&selected==null,"reset is an explicit preference action");
                data=Wait(()=>api.SelectAccountAsync("fake-bound",null,CancellationToken.None));
                strip.UpdateData(data,null);strip.SelectDefaultAccount();
                Check(data.selection.mode=="auto"&&strip.SelectedAlias==data.accounts[0].alias,"reset restores automatic order and taskbar primary");
                data.selection=null;popup.SetData(data);popup.SetPrimary(data.accounts[0].alias);
                Check(((WrapPanel)root.FindName("RoutingActions")).Visibility==Visibility.Collapsed&&(string)Choice(root).Content=="设为主显示","old-server compatibility retains display-only operation");
                Click(Choice(root));Check(display==1&&events==2,"old-server display action never changes routing");
                data.selection=new AccountSelection{mode="auto"};data.accounts[1].frozen=true;popup.SetData(data);
                Click(Choice(root));Check(display==2&&events==2,"frozen account remains inspectable but not selected for calls");
                Check(!popup.IsVisible&&!strip.Visible,"test does not replace or overlap running real helper");
            }
            Console.WriteLine("Total "+checks+" passed; no Credential Manager read, startup edit or upstream request.");return 0;
        } catch(Exception error){Console.Error.WriteLine(error.GetType().Name+": "+error.Message);return 1;}
        finally {if(popup!=null)popup.Shutdown();if(strip!=null)strip.Dispose();RegOverridePredefKey(new IntPtr(unchecked((int)0x80000001)),IntPtr.Zero);if(hive!=null)hive.Dispose();Registry.CurrentUser.DeleteSubKeyTree(isolated,false);}
    }
}
