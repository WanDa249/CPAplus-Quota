using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class VisibilityProbe
{
    private const BindingFlags Flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static Assembly app;
    private static int count;
    [DllImport("advapi32.dll")] private static extern int RegOverridePredefKey(IntPtr key,IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct Rect {public int left,top,right,bottom;}
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
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
    private static void Pump(int milliseconds)
    {
        var frame=new System.Windows.Threading.DispatcherFrame();
        var timer=new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send) {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=delegate {frame.Continue=false;};timer.Start();System.Windows.Threading.Dispatcher.PushFrame(frame);timer.Stop();
    }
    private static System.Collections.Generic.IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){
            var child=VisualTreeHelper.GetChild(parent,i);yield return child;
            foreach(var descendant in Visuals(child))yield return descendant;
        }
    }
    private static void Snapshot(FrameworkElement element,string name)
    {
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*1.5),(int)Math.Ceiling(element.ActualHeight*1.5),144,144,PixelFormats.Pbgra32);
        bitmap.Render(element);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var file=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","evidence",name+".png")))encoder.Save(file);
    }
    private static void Escape(Window window)
    {
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});
    }

    [STAThread] private static int Main(string[] args)
    {
        string registry=@"Software\CPAplusQuotaVisibilityTests\"+Guid.NewGuid().ToString("N");
        object popup=null,strip=null;RegistryKey hive=null;
        try {
            hive=Registry.CurrentUser.CreateSubKey(registry);
            Require(RegOverridePredefKey(new IntPtr(unchecked((int)0x80000001)),hive.Handle.DangerousGetHandle())==0,"settings isolated from current user");
            using(var theme=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))theme.SetValue("SystemUsesLightTheme",1);
            app=Assembly.LoadFrom(args.Length>1&&args[0]=="--app"?args[1]:Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CPAplusQuota.exe"));
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
            popup=Activator.CreateInstance(T("QuotaPopup"),true);var root=(FrameworkElement)T("QuotaPopup").GetField("root",Flags).GetValue(popup);
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
            Call("TaskbarStrip","UpdateData",strip,result,null);
            Call("TaskbarStrip","Present",strip,new System.Drawing.Rectangle(-32000,-32000,1000,48),-32000,-31000,96);
            Require(!transition.Enabled,"first real offscreen render does not animate");
            T("QuotaWindow").GetProperty("remainingPercent").SetValue(five,61d,null);Call("TaskbarStrip","UpdateData",strip,result,null);
            Call("TaskbarStrip","Present",strip,new System.Drawing.Rectangle(-32000,-32000,1000,48),-32000,-31000,96);
            Require(transition.Enabled==SystemParameters.ClientAreaAnimation,"real changed-value render follows system animation preference");
            var elapsed=System.Diagnostics.Stopwatch.StartNew();while(elapsed.ElapsedMilliseconds<300){System.Windows.Forms.Application.DoEvents();System.Threading.Thread.Sleep(10);}
            Require(!transition.Enabled&&T("TaskbarStrip").GetField("transitionFrame",Flags).GetValue(strip)==null,"real layered-window animation finishes and releases its frame");
            Require(GetForegroundWindow()!=((System.Windows.Forms.Form)strip).Handle,"real strip render and animation do not take focus");
            var week=Activator.CreateInstance(T("QuotaWindow"),true);T("QuotaWindow").GetProperty("remainingPercent").SetValue(week,61d,null);
            T("QuotaAccount").GetProperty("weekly").SetValue(account,week,null);T("QuotaAccount").GetProperty("fiveHour").SetValue(account,null,null);
            Call("TaskbarStrip","UpdateData",strip,result,null);Call("TaskbarStrip","Present",strip,new System.Drawing.Rectangle(-32000,-32000,1000,48),-32000,-31000,96);
            Require((string)T("TaskbarStrip").GetProperty("CurrentText",Flags).GetValue(strip,null)=="周 61%","weekly-only account omits nonexistent five-hour quota in real strip");
            T("QuotaAccount").GetProperty("fiveHour").SetValue(account,five,null);
            T("QuotaWindow").GetProperty("remainingPercent").SetValue(five,0d,null);
            Call("TaskbarStrip","UpdateData",strip,result,null);Call("TaskbarStrip","Present",strip,new System.Drawing.Rectangle(-32000,-32000,1000,48),-32000,-31000,96);
            Require((string)T("TaskbarStrip").GetProperty("CurrentText",Flags).GetValue(strip,null)=="5h 0% · 周 61%","exhausted five-hour quota stays visible rather than being mistaken for absent quota");
            T("QuotaWindow").GetProperty("remainingPercent").SetValue(five,61d,null);
            ((System.Windows.Forms.Form)strip).Hide();
            var extra=Activator.CreateInstance(T("QuotaAccount"),true);T("QuotaAccount").GetProperty("alias").SetValue(extra,"backup-test",null);accountList.Add(extra);
            T("QuotaAccount").GetProperty("stale").SetValue(account,false,null);
            Call("QuotaPopup","SetData",popup,result);
            var window=(Window)popup;window.ShowActivated=false;window.Topmost=true;window.Left=-32000;window.Top=-32000;window.Show();Pump(350);
            double restingHeight=window.ActualHeight,buttonWidth=refresh.ActualWidth;
            Call("QuotaPopup","SetBusy",popup,true);Call("QuotaPopup","SetReadFeedback",popup,"正在查询额度…");Pump(250);
            Require(Math.Abs(window.ActualHeight-restingHeight)<0.5&&Math.Abs(refresh.ActualWidth-buttonWidth)<0.5,"real refresh progress preserves popup height and button width");
            Console.WriteLine("MEASURE stable refresh DIP before="+restingHeight+" busy="+window.ActualHeight+" buttonWidth="+buttonWidth);
            Call("QuotaPopup","SetData",popup,result);Call("QuotaPopup","SetReadFeedback",popup,"额度已更新。");Call("QuotaPopup","SetBusy",popup,false);Pump(250);
            Require(Math.Abs(window.ActualHeight-restingHeight)<0.5,"real refresh completion preserves popup height");
            T("QuotaAccount").GetProperty("stale").SetValue(account,true,null);Call("QuotaPopup","SetData",popup,result);Call("QuotaPopup","SetReadFeedback",popup,"额度查询失败，保留上次结果；未更新成功时间。");Pump(250);
            Require(Math.Abs(window.ActualHeight-restingHeight)<0.5,"stale and failed feedback preserve popup height");
            var heights=new System.Collections.Generic.List<double>();window.SizeChanged+=delegate {heights.Add(window.ActualHeight);};
            var others=(Expander)root.FindName("OtherAccounts");others.IsExpanded=true;window.UpdateLayout();double expansionStart=window.ActualHeight;Pump(45);double expansionMid=window.ActualHeight;Pump(250);double expandedHeight=window.ActualHeight;
            Console.WriteLine("MEASURE popup DIP resting="+restingHeight+" expansionStart="+expansionStart+" mid="+expansionMid+" expanded="+expandedHeight+" root="+root.ActualHeight+" desired="+root.DesiredSize.Height+" align="+root.VerticalAlignment+" valid="+root.IsMeasureValid+" expander="+others.ActualHeight);
            Require(expandedHeight>restingHeight+10,"real account expansion grows popup to fit content");
            Require(heights.Exists(value=>value>restingHeight+0.5&&value<expandedHeight-0.5),"real expansion passes through intermediate height");
            others.IsExpanded=false;Pump(300);Require(Math.Abs(window.ActualHeight-restingHeight)<0.5,"collapse returns to original height");
            Call("QuotaPopup","Settings",popup,true);window.UpdateLayout();Pump(300);double settingsHeight=window.ActualHeight;
            Require(Math.Abs(settingsHeight-root.ActualHeight)<0.5,"settings transition finishes at measured content height");
            Call("QuotaPopup","Settings",popup,false);window.UpdateLayout();Pump(300);
            Require(Math.Abs(window.ActualHeight-restingHeight)<0.5,"return from settings restores home height");
            others.IsExpanded=true;window.UpdateLayout();Pump(45);double interruptedHeight=window.ActualHeight;
            others.IsExpanded=false;window.UpdateLayout();double resumedHeight=window.ActualHeight;
            Require(Math.Abs(resumedHeight-interruptedHeight)<2,"rapid reversal starts from current height without a jump");Pump(300);
            Console.WriteLine("MEASURE reversal final="+window.ActualHeight+" base="+window.GetAnimationBaseValue(Window.HeightProperty)+" animated="+window.HasAnimatedProperties+" root="+root.ActualHeight);
            Require(Math.Abs(window.ActualHeight-restingHeight)<0.5&&!window.HasAnimatedProperties,"rapid reversal completes and animation clocks are released");
            if(args.Length>0&&args[0]=="--geometry"){
                ((TextBlock)root.FindName("Heading")).Text="CPAplus 本地动画验证";
                window.Topmost=false;Call("QuotaPopup","PositionPopup",popup);Pump(300);
                var hwnd=new System.Windows.Interop.WindowInteropHelper(window).Handle;Rect start;GetWindowRect(hwnd,out start);
                var samples=new System.Collections.Generic.List<string>();var clock=System.Diagnostics.Stopwatch.StartNew();string phase="expand";int maxNative=0,maxRender=0,maxRefresh=0,refreshHeight=0;
                Action<string> sample=kind=>{Rect r;GetWindowRect(hwnd,out r);int delta=Math.Abs(r.bottom-start.bottom);if(kind=="native")maxNative=Math.Max(maxNative,delta);else maxRender=Math.Max(maxRender,delta);if(phase=="expanded-refresh")maxRefresh=Math.Max(maxRefresh,Math.Abs(r.bottom-r.top-refreshHeight));samples.Add(clock.Elapsed.TotalMilliseconds.ToString("F2",System.Globalization.CultureInfo.InvariantCulture)+","+phase+","+kind+","+r.top+","+r.bottom+","+(r.bottom-r.top));};
                System.Windows.Interop.HwndSourceHook hook=delegate(IntPtr h,int message,IntPtr w,IntPtr l,ref bool handled){if(message==0x47)sample("native");return IntPtr.Zero;};
                var source=System.Windows.Interop.HwndSource.FromHwnd(hwnd);source.AddHook(hook);EventHandler rendered=delegate {sample("render");};System.Windows.Media.CompositionTarget.Rendering+=rendered;
                try {
                    others.IsExpanded=true;Pump(350);double openHeight=window.ActualHeight;Rect openBounds;GetWindowRect(hwnd,out openBounds);refreshHeight=openBounds.bottom-openBounds.top;phase="expanded-refresh";
                    Call("QuotaPopup","SetBusy",popup,true);Call("QuotaPopup","SetReadFeedback",popup,"正在查询额度…");Pump(60);
                    Call("QuotaPopup","SetData",popup,result);Call("QuotaPopup","SetPrimary",popup,"test-account");Call("QuotaPopup","SetReadFeedback",popup,"额度已更新。");Call("QuotaPopup","SetBusy",popup,false);Pump(350);
                    Require(Math.Abs(window.ActualHeight-openHeight)<0.5,"expanded account refresh preserves popup height");
                    phase="collapse";others.IsExpanded=false;Pump(350);
                }finally{source.RemoveHook(hook);System.Windows.Media.CompositionTarget.Rendering-=rendered;}
                string output=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","evidence",args.Length>1?args[1]:"native-geometry.csv"));
                File.WriteAllLines(output,new[]{"ms,phase,kind,top,bottom,height"}.Concat(samples));
                Console.WriteLine("GEOMETRY samples="+samples.Count+" maxNativeBottomDriftPx="+maxNative+" maxRenderBottomDriftPx="+maxRender+" maxExpandedRefreshHeightDriftPx="+maxRefresh);
                Require(maxNative==0&&maxRender==0,"native and rendered bottom edge remains fixed during expand and collapse");
                Require(maxRefresh==0,"expanded refresh has no transient height change in any recorded frame");
            }
            for(int i=0;i<3;i++){
                var other=Activator.CreateInstance(T("QuotaAccount"),true);
                T("QuotaAccount").GetProperty("alias").SetValue(other,"layout-"+i,null);
                T("QuotaAccount").GetProperty("displayName").SetValue(other,"very-long-account-name-"+i+"@example.invalid",null);
                foreach(string flag in new[]{"available","availabilityKnown","supported"})T("QuotaAccount").GetProperty(flag).SetValue(other,true,null);
                T("QuotaAccount").GetProperty("weekly").SetValue(other,week,null);
                if(i!=2)T("QuotaAccount").GetProperty("fiveHour").SetValue(other,five,null);
                T("QuotaAccount").GetProperty("stale").SetValue(other,i==1,null);accountList.Add(other);
            }
            Call("QuotaPopup","SetData",popup,result);others.IsExpanded=true;Pump(350);
            var rows=(StackPanel)root.FindName("AccountRows");
            foreach(double width in new[]{320d,368d})foreach(double scale in new[]{1d,1.25d,1.5d}){
                root.LayoutTransform=new ScaleTransform(scale,scale);window.Width=width*scale;Pump(350);
                foreach(Expander row in rows.Children){
                    var header=(Grid)row.Header;var summary=(TextBlock)header.Children[1];
                    var arrow=Visuals(row).OfType<TextBlock>().Single(text=>text.Name=="Chevron");
                    var headerBounds=header.TransformToAncestor(row).TransformBounds(new System.Windows.Rect(0,0,header.ActualWidth,header.ActualHeight));
                    var summaryBounds=summary.TransformToAncestor(row).TransformBounds(new System.Windows.Rect(0,0,summary.ActualWidth,summary.ActualHeight));
                    var arrowBounds=arrow.TransformToAncestor(row).TransformBounds(new System.Windows.Rect(0,0,arrow.ActualWidth,arrow.ActualHeight));
                    Require(summaryBounds.Right<=arrowBounds.Left&&headerBounds.Right<=row.ActualWidth,"summary fits before chevron at "+width+" DIP / "+scale+" scale");
                }
            }
            root.LayoutTransform=Transform.Identity;window.Width=368;Pump(350);Snapshot(root,"accounts-collapsed");
            ((Expander)rows.Children[2]).IsExpanded=true;Pump(350);
            var scroll=(ScrollViewer)root.FindName("OtherScroll");
            Require(scroll.ScrollableHeight>0,"expanded multi-account list scrolls within a bounded viewport");
            var scrollbar=Visuals(scroll).OfType<System.Windows.Controls.Primitives.ScrollBar>().First(bar=>bar.Orientation==Orientation.Vertical);
            Console.WriteLine("MEASURE scrollbar width="+scrollbar.ActualWidth+" min="+scrollbar.MinWidth+" template="+scrollbar.Template);
            Require(scrollbar.ActualWidth<=6.5,"account scrollbar uses narrow unobtrusive track");
            scroll.ScrollToBottom();Pump(100);Require(scroll.VerticalOffset>0,"styled scrollbar retains real scroll behavior");scroll.ScrollToTop();Pump(100);
            Snapshot(root,"accounts-expanded");double expandedRest=window.ActualHeight;
            var retainedRow=(Expander)rows.Children[2];var retainedButton=((StackPanel)retainedRow.Content).Children.OfType<Button>().Single();
            scroll.ScrollToBottom();Pump(80);double retainedOffset=scroll.VerticalOffset;
            FocusManager.SetFocusedElement(window,retainedButton);
            Call("QuotaPopup","SetBusy",popup,true);Call("QuotaPopup","SetData",popup,result);Call("QuotaPopup","SetBusy",popup,false);Pump(350);
            Require(Math.Abs(window.ActualHeight-expandedRest)<0.5,"new account cards preserve expanded height during refresh");
            Require(object.ReferenceEquals(rows.Children[2],retainedRow)&&object.ReferenceEquals(((StackPanel)retainedRow.Content).Children.OfType<Button>().Single(),retainedButton),"background update retains actual account and action controls");
            Require(retainedRow.IsExpanded&&Math.Abs(scroll.VerticalOffset-retainedOffset)<0.5&&object.ReferenceEquals(FocusManager.GetFocusedElement(window),retainedButton),"background update retains expansion, scroll position and logical focus");
            var renderTimes=new System.Collections.Generic.List<double>();
            for(int pass=0;pass<30;pass++){var clock=System.Diagnostics.Stopwatch.StartNew();Call("QuotaPopup","SetData",popup,result);window.UpdateLayout();clock.Stop();renderTimes.Add(clock.Elapsed.TotalMilliseconds);}
            renderTimes.Sort();Console.WriteLine("MEASURE account update and layout ms median="+renderTimes[15].ToString("F3")+" max="+renderTimes[29].ToString("F3"));
            var selection=Activator.CreateInstance(T("AccountSelection"),true);T("AccountSelection").GetProperty("mode").SetValue(selection,"auto",null);T("QuotaResult").GetProperty("selection").SetValue(result,selection,null);
            Call("QuotaPopup","SetData",popup,result);string requestedAlias=null;
            T("QuotaPopup").GetEvent("PreferAccountRequested",Flags).GetAddMethod(true).Invoke(popup,new object[]{new Action<string>(alias=>{requestedAlias=alias;Call("QuotaPopup","SetBusy",popup,true);})});
            retainedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(requestedAlias=="layout-1"&&others.IsExpanded&&retainedRow.IsExpanded&&(string)retainedButton.Content=="切换中…","account selection acknowledges click immediately and keeps comparison list open");
            requestedAlias=null;retainedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(requestedAlias==null,"pending account action cannot be submitted twice");Call("QuotaPopup","SetBusy",popup,false);
            Require(((StackPanel)retainedRow.Content).Children.IndexOf(retainedButton)==1,"account action is reachable above quota details without scrolling to the bottom");
            double beforeSettingsOffset=scroll.VerticalOffset;
            ((Button)root.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Escape(window);Pump(280);
            Require(window.IsVisible&&((FrameworkElement)root.FindName("HomePage")).Visibility==Visibility.Visible,"back from settings returns to quota instead of dismissing popup");
            Require(Math.Abs(scroll.VerticalOffset-beforeSettingsOffset)<0.5,"settings return preserves account list scroll position");
            Call("QuotaPopup","ShowKeyEditor",popup,"fake-test-key");((PasswordBox)root.FindName("KeyInput")).Password="fake-unsaved-key";Escape(window);Pump(280);
            Require(((FrameworkElement)root.FindName("SettingsPage")).Visibility==Visibility.Visible&&((PasswordBox)root.FindName("KeyInput")).Password.Length==0,"back from key editor returns one level and clears sensitive draft");
            ((Button)root.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(280);Call("QuotaPopup","TogglePopup",popup);Require(!window.IsVisible,"second click on quota entry dismisses home popup");window.Show();Pump(280);
            Call("QuotaPopup","SetPrimary",popup,"layout-2");others.IsExpanded=false;Pump(350);
            Require(((FrameworkElement)root.FindName("FiveMetric")).Visibility==Visibility.Collapsed,"weekly-only primary card hides absent five-hour row");Snapshot(root,"weekly-only");
            others.IsExpanded=true;window.UpdateLayout();Pump(30);window.Hide();Require(!window.HasAnimatedProperties,"hiding popup clears resize animation");
            Require(GetForegroundWindow()!=new System.Windows.Interop.WindowInteropHelper(window).Handle,"popup layout tests do not take focus");
            var fresh=(Window)Activator.CreateInstance(T("QuotaPopup"),true);fresh.ShowActivated=false;
            try {
                Call("QuotaPopup","SetData",fresh,result);double initialLeft=double.NaN,initialTop=double.NaN;
                var freshRoot=(FrameworkElement)T("QuotaPopup").GetField("root",Flags).GetValue(fresh);
                Console.WriteLine("MEASURE hidden root font="+System.Windows.Documents.TextElement.GetFontFamily(freshRoot)+" rounding="+freshRoot.UseLayoutRounding+" dpi="+VisualTreeHelper.GetDpi(freshRoot).DpiScaleX);
                fresh.IsVisibleChanged+=delegate {if(fresh.IsVisible){initialLeft=fresh.Left;initialTop=fresh.Top;Console.WriteLine("MEASURE first visible DIP left="+initialLeft+" top="+initialTop+" height="+fresh.Height);}};
                Call("QuotaPopup","ShowPopup",fresh);Pump(300);
                Console.WriteLine("MEASURE first settled DIP left="+fresh.Left+" top="+fresh.Top+" height="+fresh.Height);
                Console.WriteLine("MEASURE visible root font="+System.Windows.Documents.TextElement.GetFontFamily(freshRoot)+" rounding="+freshRoot.UseLayoutRounding+" dpi="+VisualTreeHelper.GetDpi(freshRoot).DpiScaleX);
                Require(Math.Abs(initialLeft-fresh.Left)<0.5&&Math.Abs(initialTop-fresh.Top)<0.5,"brand new popup is placed before first visibility without a later snap");
                Require(GetForegroundWindow()!=new System.Windows.Interop.WindowInteropHelper(fresh).Handle,"first popup positioning does not activate test window");
            }finally {Call("QuotaPopup","Shutdown",fresh);}
            window.Topmost=false;others.IsExpanded=false;Pump(300);
            double firstLeft=double.NaN,firstTop=double.NaN;
            DependencyPropertyChangedEventHandler shown=delegate {if(window.IsVisible){firstLeft=window.Left;firstTop=window.Top;}};
            window.IsVisibleChanged+=shown;
            Call("QuotaPopup","ShowPopup",popup);Pump(300);
            Require(Math.Abs(firstLeft-window.Left)<0.5&&Math.Abs(firstTop-window.Top)<0.5,"default position is final before window becomes visible");
            var handle=new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var transform=PresentationSource.FromVisual(window).CompositionTarget.TransformFromDevice;
            var area=System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            double movedLeft=area.Left*transform.M11+60,movedTop=area.Top*transform.M22+45;
            // Exercise the real native interactive-move notification, without mouse injection.
            SendMessage(handle,0x231,IntPtr.Zero,IntPtr.Zero);
            window.Left=movedLeft;window.Top=movedTop;
            SendMessage(handle,0x232,IntPtr.Zero,IntPtr.Zero);Pump(50);
            ((Button)root.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!window.IsVisible,"X hides the popup without destroying its position");
            Call("QuotaPopup","ShowPopup",popup);Pump(300);
            Require(Math.Abs(window.Left-movedLeft)<1&&Math.Abs(window.Top-movedTop)<1,"X then taskbar reopen preserves manually moved location");
            Require(Math.Abs(firstLeft-movedLeft)<1&&Math.Abs(firstTop-movedTop)<1,"moved position is restored before first visible frame");
            others.IsExpanded=true;Pump(300);
            Require(Math.Abs(window.Left-movedLeft)<1&&Math.Abs(window.Top-movedTop)<1,"expansion keeps manually placed popup top-left stable");
            Call("QuotaPopup","Settings",popup,true);Pump(300);Call("QuotaPopup","Settings",popup,false);Pump(300);
            Require(Math.Abs(window.Left-movedLeft)<1&&Math.Abs(window.Top-movedTop)<1,"settings navigation preserves manually placed location");
            Call("QuotaPopup","SetData",popup,result);Pump(300);
            Require(Math.Abs(window.Left-movedLeft)<1&&Math.Abs(window.Top-movedTop)<1,"background quota update does not reset user position");
            window.Hide();window.Left=-32000;window.Top=-32000;
            Call("QuotaPopup","ShowPopup",popup);Pump(300);
            area=System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            Rect visibleBounds;GetWindowRect(handle,out visibleBounds);
            Require(visibleBounds.left>=area.Left&&visibleBounds.top>=area.Top&&visibleBounds.right<=area.Right&&visibleBounds.bottom<=area.Bottom,"reopening clamps unavailable screen position into current work area");
            Require(Math.Abs(firstLeft-window.Left)<1&&Math.Abs(firstTop-window.Top)<1,"offscreen recovery finishes before first visible frame");
            window.IsVisibleChanged-=shown;window.Hide();
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
