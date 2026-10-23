using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Point=System.Drawing.Point;
using Rectangle=System.Drawing.Rectangle;

internal static class DockProbe
{
    [DllImport("advapi32.dll")] private static extern int RegOverridePredefKey(IntPtr key,IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static int checks;
    private static T Field<T>(object target,string name){return (T)target.GetType().GetField(name,Flags).GetValue(target);}
    private static void Check(bool yes,string label){if(!yes)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
    private static void Pump(int ms=230){var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=delegate {frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();}
    private static Rectangle Bounds(Window window){DesktopFacts.Rect r;if(!DesktopFacts.GetWindowRect(new WindowInteropHelper(window).Handle,out r))throw new Exception("Window bounds unavailable");return r.Rectangle;}
    private static Point Center(Rectangle r){return new Point(r.Left+r.Width/2,r.Top+r.Height/2);}
    private static void Drive(PopupEdgeDock dock,Point cursor,long time,bool fullscreen=false){var timer=Field<DispatcherTimer>(dock,"timer");timer.Start();dock.Advance(cursor,time,fullscreen);timer.Stop();}
    private static void Click(FrameworkElement root,string name){((Button)root.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
    private static void Toggle(FrameworkElement root,bool value){var option=(CheckBox)root.FindName("EdgeSetting");option.IsChecked=value;option.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
    private static bool Draggable(QuotaPopup popup,DependencyObject source){return (bool)typeof(QuotaPopup).GetMethod("CanDragFrom",Flags).Invoke(popup,new object[]{source});}
    private static void Drag(QuotaPopup popup,bool edge,bool left=false)
    {
        var handle=new WindowInteropHelper(popup).Handle;
        var screen=left?System.Windows.Forms.Screen.AllScreens.OrderBy(s=>s.Bounds.Left).First():System.Windows.Forms.Screen.AllScreens.OrderByDescending(s=>s.Bounds.Right).First();var area=screen.WorkingArea;
        SendMessage(handle,0x231,IntPtr.Zero,IntPtr.Zero);
        DesktopFacts.SetWindowPos(handle,IntPtr.Zero,area.Right-500,area.Top+45,0,0,0x15);Pump(30);
        var rect=Bounds(popup);DesktopFacts.SetWindowPos(handle,IntPtr.Zero,edge?(left?area.Left+4:area.Right-rect.Width-4):area.Left+80,area.Top+45,0,0,0x15);
        SendMessage(handle,0x232,IntPtr.Zero,IntPtr.Zero);
    }
    [STAThread] private static int Main(string[] args)
    {
        string registry=@"Software\CPAplusQuotaDockTests\"+Guid.NewGuid().ToString("N");RegistryKey hive=null;QuotaPopup popup=null;
        try {
            hive=Registry.CurrentUser.CreateSubKey(registry);
            Check(RegOverridePredefKey(new IntPtr(unchecked((int)0x80000001)),hive.Handle.DangerousGetHandle())==0,"registry and preferences isolated");
            using(var key=Registry.CurrentUser.CreateSubKey(@"Software\CPAplusQuota"))key.SetValue("ServerUrl","https://quota.example.invalid");
            var monitor=new Rectangle(0,0,1920,1080);var panel=new Rectangle(3,200,368,440);
            Check(PopupEdgeDock.ChooseSide(panel,monitor,monitor,new[]{monitor},16)==-1,"left exterior edge arms docking");
            Check(PopupEdgeDock.ChooseSide(new Rectangle(1548,200,368,440),monitor,monitor,new[]{monitor},16)==1,"right exterior edge arms docking");
            Check(PopupEdgeDock.ChooseSide(new Rectangle(200,200,368,440),monitor,monitor,new[]{monitor},16)==0,"ordinary positions never arm docking");
            Check(PopupEdgeDock.ChooseSide(panel,monitor,monitor,new[]{monitor,new Rectangle(-1920,0,1920,1080)},16)==0,"shared left monitor seam does not hide onto another screen");
            Check(PopupEdgeDock.ChooseSide(new Rectangle(1548,200,368,440),monitor,monitor,new[]{monitor,new Rectangle(1920,0,1920,1080)},16)==0,"shared right monitor seam is excluded");
            Check(PopupEdgeDock.ChooseSide(panel,monitor,monitor,new[]{monitor,new Rectangle(-1920,-1080,1920,1080)},16)==-1,"nonoverlapping vertically offset monitor does not block outer edge");
            var inset=Rectangle.Inflate(monitor,-12,-12);
            Func<Rectangle,Rectangle> snap=r=>PopupEdgeDock.SnapBounds(r,monitor,monitor,new[]{monitor},12,10);
            Check(snap(new Rectangle(1700,200,368,440)).Right==inset.Right,"right overflow beyond snap range returns fully inside");
            Check(snap(new Rectangle(-100,200,368,440)).Left==inset.Left,"left overflow beyond snap range returns fully inside");
            Check(snap(new Rectangle(200,-100,368,440)).Top==inset.Top,"top overflow beyond snap range returns fully inside");
            Check(snap(new Rectangle(200,900,368,440)).Bottom==inset.Bottom,"bottom overflow beyond snap range returns fully inside");
            Check(inset.Contains(snap(new Rectangle(1700,900,368,440))),"overflow at two edges is corrected together");
            Check(inset.Contains(snap(new Rectangle(1553,200,368,440))),"even one physical pixel outside is recovered");
            Check(PopupEdgeDock.SnapBounds(new Rectangle(200,899,368,440),new Rectangle(0,0,1920,1040),monitor,new[]{monitor},12,18).Bottom==1028,"overflow recovery avoids the reserved taskbar area");
            var negativeScreen=new Rectangle(-1920,-1080,1920,1080);
            Check(PopupEdgeDock.SnapBounds(new Rectangle(-2100,-1200,368,440),negativeScreen,negativeScreen,new[]{negativeScreen},12,18).Location==new Point(-1908,-1068),"overflow recovery supports negative screen coordinates");
            Check(PopupEdgeDock.SnapBounds(new Rectangle(29,200,368,440),monitor,monitor,new[]{monitor},12,18).Left==12,"18 DIP release attraction is perceptible before actual overflow");
            Check(monitor.Contains(snap(new Rectangle(-1,-1,1910,1070))),"almost full-screen popup uses available area when decorative margins cannot fit");
            Check(snap(new Rectangle(21,200,368,440)).Left==12,"near left edge gently snaps to inset");
            Check(snap(new Rectangle(1532,200,368,440)).Right==inset.Right,"near right edge gently snaps to inset");
            Check(snap(new Rectangle(200,21,368,440)).Top==12,"near top edge snaps without covering screen boundary");
            Check(snap(new Rectangle(200,635,368,440)).Bottom==inset.Bottom,"near bottom edge snaps to usable inset");
            Check(snap(new Rectangle(21,21,368,440)).Location==new Point(12,12),"corners snap on both axes");
            var free=new Rectangle(23,200,368,440);
            Check(snap(free)==free,"release outside supplied attraction range keeps free placement");
            Check(snap(new Rectangle(21,200,368,440)).Size==panel.Size,"snap never resizes the popup");
            Check(PopupEdgeDock.SnapBounds(new Rectangle(21,200,368,440),monitor,monitor,new[]{monitor,new Rectangle(-1920,0,1920,1080)},12,10).Left==21,"horizontal monitor seam permits crossing");
            Check(PopupEdgeDock.SnapBounds(new Rectangle(200,21,368,440),monitor,monitor,new[]{monitor,new Rectangle(0,-1080,1920,1080)},12,10).Top==21,"vertical monitor seam permits crossing");
            Check(PopupEdgeDock.SnapBounds(new Rectangle(200,635,368,440),monitor,monitor,new[]{monitor,new Rectangle(0,1080,1920,1080)},12,10).Top==635,"lower monitor seam permits crossing");
            var workArea=new Rectangle(0,0,1920,1040);
            Check(PopupEdgeDock.SnapBounds(new Rectangle(200,597,368,440),workArea,monitor,new[]{monitor},12,10).Bottom==1028,"bottom snap respects taskbar reserved work area");
            var negative=new Rectangle(-1920,-1080,1920,1080);
            Check(PopupEdgeDock.SnapBounds(new Rectangle(-1899,-1059,368,440),negative,negative,new[]{negative},12,10).Location==new Point(-1908,-1068),"negative monitor coordinates snap correctly");
            var oversized=new Rectangle(21,21,2000,1200);
            Check(snap(oversized)==oversized,"oversized popup is not forced onto an unusable edge");
            foreach(double dpi in new[]{1.0,1.25,1.5}){
                int margin=(int)Math.Ceiling(12*dpi),distance=(int)Math.Ceiling(10*dpi);
                var proposed=new Rectangle(margin+distance,200,368,440);
                Check(PopupEdgeDock.SnapBounds(proposed,monitor,monitor,new[]{monitor},margin,distance).Left==margin,"attraction threshold scales at "+dpi+" DPI factor");
            }
            popup=new QuotaPopup {ShowActivated=false,Title="CPAplus 贴边本地验证"};
            var root=Field<FrameworkElement>(popup,"root");var dock=Field<PopupEdgeDock>(popup,"edgeDock");var timer=Field<DispatcherTimer>(dock,"timer");
            Check(!dock.Enabled&&!timer.IsEnabled&&Field<Window>(dock,"tab")==null,"default off creates no handle and runs no dock timer");
            popup.SetData(new QuotaResult {accounts=new System.Collections.Generic.List<QuotaAccount>{new QuotaAccount {alias="dock-test",displayName="本地贴边验证",supported=true,available=true,availabilityKnown=true,weekly=new QuotaWindow {remainingPercent=42}}}});
            popup.ShowPopup();Pump();var handle=new WindowInteropHelper(popup).Handle;
            var proposedMove=new DesktopFacts.Rect {Left=Bounds(popup).Left,Top=Bounds(popup).Top,Right=Bounds(popup).Right,Bottom=Bounds(popup).Bottom};
            var beforeProposed=Bounds(popup);
            var moveMemory=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(DesktopFacts.Rect)));
            try {
                Marshal.StructureToPtr(proposedMove,moveMemory,false);
                object[] hookArgs={handle,0x216,IntPtr.Zero,moveMemory,false};
                var result=typeof(QuotaPopup).GetMethod("KeepResizeAnchor",Flags).Invoke(popup,hookArgs);
                Check((IntPtr)result==IntPtr.Zero&&!(bool)hookArgs[4],"WM_MOVING remains unhandled so native drag owns both axes");
                Check(((DesktopFacts.Rect)Marshal.PtrToStructure(moveMemory,typeof(DesktopFacts.Rect))).Rectangle==proposedMove.Rectangle,"WM_MOVING never rewrites the pointer's proposed rectangle");
                Check(Bounds(popup)==beforeProposed,"WM_MOVING hook never repositions the window");
            }finally {Marshal.FreeHGlobal(moveMemory);}
            var screenArea=System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var actualScale=HwndSource.FromHwnd(handle).CompositionTarget.TransformToDevice;
            int snapMargin=(int)Math.Ceiling(12*actualScale.M11),snapRange=(int)Math.Ceiling(18*actualScale.M11);
            SendMessage(handle,0x231,IntPtr.Zero,IntPtr.Zero);
            DesktopFacts.SetWindowPos(handle,IntPtr.Zero,screenArea.Left+snapMargin+3,screenArea.Top+snapMargin+3,0,0,0x15);Pump(30);
            Check(Bounds(popup).Left==screenArea.Left+snapMargin+3&&Bounds(popup).Top==screenArea.Top+snapMargin+3,"both axes remain freely positioned inside attraction range while dragging");
            var dragScreen=System.Windows.Forms.Screen.FromHandle(handle);
            var expectedCorner=PopupEdgeDock.SnapBounds(Bounds(popup),dragScreen.WorkingArea,dragScreen.Bounds,System.Windows.Forms.Screen.AllScreens.Select(s=>s.Bounds).ToArray(),snapMargin,snapRange);
            SendMessage(handle,0x232,IntPtr.Zero,IntPtr.Zero);Pump(30);
            Console.WriteLine("MEASURE release corner actual="+Bounds(popup)+" expected="+expectedCorner+" monitors="+System.Windows.Forms.Screen.AllScreens.Length);
            Check(Bounds(popup)==expectedCorner,"release aligns only exterior edges and respects actual monitor seams");
            Check(!dock.Docked&&!timer.IsEnabled,"release alignment does not enable automatic hiding");
            SendMessage(handle,0x231,IntPtr.Zero,IntPtr.Zero);
            int releasedX=screenArea.Left+snapMargin+snapRange+4,releasedY=screenArea.Top+snapMargin+snapRange+4;
            DesktopFacts.SetWindowPos(handle,IntPtr.Zero,releasedX,releasedY,0,0,0x15);Pump(30);
            SendMessage(handle,0x232,IntPtr.Zero,IntPtr.Zero);Pump(30);
            Check(Bounds(popup).Location==new Point(releasedX,releasedY),"release outside the 18 DIP range leaves exact user placement untouched");
            SendMessage(handle,0x231,IntPtr.Zero,IntPtr.Zero);
            DesktopFacts.SetWindowPos(handle,IntPtr.Zero,screenArea.Right-Bounds(popup).Width+90,screenArea.Bottom-Bounds(popup).Height+70,0,0,0x15);Pump(30);
            Check(Bounds(popup).Right>screenArea.Right&&Bounds(popup).Bottom>screenArea.Bottom,"drag may freely overshoot right and bottom while mouse is held");
            SendMessage(handle,0x232,IntPtr.Zero,IntPtr.Zero);Pump(30);
            Check(Bounds(popup).Right==screenArea.Right-snapMargin&&Bounds(popup).Bottom==screenArea.Bottom-snapMargin,"native drag release recovers the entire window from right and bottom overflow");
            Check(!dock.Docked&&!timer.IsEnabled,"overflow recovery also works with auto-hide disabled");
            Toggle(root,true);
            if(args.Contains("--preview")){popup.ShowInTaskbar=true;Drag(popup,true);timer.Stop();Pump(300000);return 0;}
            {
                foreach(bool left in new[]{true,false}){
                    Drag(popup,true,left);timer.Stop();Pump(350);
                    var edgeArea=Field<Rectangle>(dock,"area");
                    var edgeScale=HwndSource.FromHwnd(handle).CompositionTarget.TransformFromDevice;
                    popup.Top=edgeArea.Bottom*edgeScale.M22-popup.Height-12;Pump(50);
                    var edgeAnchor=Bounds(popup);
                    Console.WriteLine("MEASURE dock left="+left+" area="+edgeArea+" popup="+edgeAnchor+" scale="+HwndSource.FromHwnd(handle).CompositionTarget.TransformFromDevice);
                    Check(dock.Docked,"real exterior edge docks; left="+left);
                    Click(root,"SettingsButton");Pump(350);popup.ShowKeyEditor("fake-edge-key");Pump(350);
                    Click(root,"BackButton");Pump(350);Click(root,"BackButton");Pump(350);
                    Check(Math.Abs(Bounds(popup).Top-edgeAnchor.Top)<=1,"docked settings/key return restores original bottom placement; left="+left);
                    timer.Stop();dock.PrepareShow();
                    for(int cycle=0;cycle<3;cycle++){
                        long time=cycle*3000;var away=new Point(edgeArea.Left+edgeArea.Width/2,edgeArea.Bottom-5);
                        Drive(dock,away,time);Drive(dock,away,time+600);Pump(300);
                        var edgeTab=Field<Window>(dock,"tab");
                        Console.WriteLine("MEASURE stowed left="+left+" cycle="+cycle+" tab="+Bounds(edgeTab)+" popup="+Bounds(popup)+" tabVisible="+edgeTab.IsVisible);
                        Check(!popup.IsVisible&&edgeTab.IsVisible&&edgeArea.Contains(Bounds(edgeTab)),"edge handle remains reachable; left="+left+" cycle="+cycle);
                        var hover=Center(Bounds(edgeTab));Drive(dock,hover,time+1000);Drive(dock,hover,time+1300);Pump(300);
                        Console.WriteLine("MEASURE expanded left="+left+" cycle="+cycle+" popup="+Bounds(popup));
                        Check(popup.IsVisible&&!edgeTab.IsVisible&&edgeArea.Contains(Bounds(popup))&&Math.Abs(Bounds(popup).Left-edgeAnchor.Left)<=1,"hover fully restores edge panel; left="+left+" cycle="+cycle);
                    }
                }
                if(args.Contains("--edges-only"))return 0;
                Drag(popup,false);timer.Stop();Pump(300);
            }
            foreach(string name in new[]{"HomePage","MainCard","AccountName","AccountState","Heading"})
                Check(Draggable(popup,(DependencyObject)root.FindName(name)),name+" permits dragging from passive content");
            foreach(string name in new[]{"CloseButton","SettingsButton","RefreshButton","NoteInput","ServerInput","KeyInput","DisplayMode","EdgeSetting","OtherAccounts"})
                Check(!Draggable(popup,(DependencyObject)root.FindName(name)),name+" keeps its own interaction");
            var settings=(Button)root.FindName("SettingsButton");
            Check(!Draggable(popup,System.Windows.Media.VisualTreeHelper.GetChild(settings,0)),"button template content is excluded from drag");
            Check(!Draggable(popup,null),"missing input source does not start drag");
            Check(dock.Enabled&&!dock.Docked&&!timer.IsEnabled,"enabling alone leaves popup untouched and idle");
            Check((int)Registry.GetValue(@"HKEY_CURRENT_USER\Software\CPAplusQuota","EdgeAutoHide",0)==1,"choice persists in existing user settings");
            var next=new QuotaPopup();try {Check(Field<PopupEdgeDock>(next,"edgeDock").Enabled,"next popup restores enabled preference without automatically docking");}finally {next.Shutdown();}
            Drag(popup,true);timer.Stop();
            Check(dock.Docked&&popup.Topmost,"real native drag completion arms docking and keeps expanded panel accessible");
            var beforeMove=Bounds(popup);
            var returned=new System.Threading.ManualResetEventSlim();
            var input=System.Threading.Tasks.Task.Run(delegate {
                System.Threading.Thread.Sleep(80);
                for(int i=0;i<8;i++){PostMessage(handle,0x100,new IntPtr(0x25),IntPtr.Zero);System.Threading.Thread.Sleep(15);}
                PostMessage(handle,0x100,new IntPtr(0x0D),IntPtr.Zero);
                if(!returned.Wait(500))PostMessage(handle,0x100,new IntPtr(0x1B),IntPtr.Zero);
            });
            SendMessage(handle,0x112,new IntPtr(0xF010),IntPtr.Zero);returned.Set();input.GetAwaiter().GetResult();returned.Dispose();Pump(50);timer.Stop();
            Console.WriteLine("MEASURE native move before="+beforeMove.Left+" after="+Bounds(popup).Left+" docked="+dock.Docked);
            Check(Bounds(popup).Left<beforeMove.Left-30&&!dock.Docked,"complete native move loop releases dock and moves panel away");
            Drag(popup,true);timer.Stop();
            var anchor=Bounds(popup);var outside=new Point(anchor.Left-100,anchor.Top+10);
            Drive(dock,outside,0);Drive(dock,outside,599);Check(popup.IsVisible&&!Field<bool>(dock,"moving"),"leaving does not collapse before 600ms");
            Drive(dock,Center(anchor),600);Drive(dock,outside,700);Drive(dock,outside,1299);
            Check(popup.IsVisible&&!Field<bool>(dock,"moving"),"returning pointer cancels old leave deadline");
            Drive(dock,outside,1300);Check(Field<bool>(dock,"moving"),"600ms leave starts smooth retract");Pump();
            var tab=Field<Window>(dock,"tab");Check(!popup.IsVisible&&tab.IsVisible&&Field<bool>(dock,"stowed"),"retract leaves only a compact edge handle");
            var work=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(tab).Handle).WorkingArea;
            var tabScale=HwndSource.FromHwnd(new WindowInteropHelper(tab).Handle).CompositionTarget.TransformFromDevice;
            Check(work.Contains(Bounds(tab))&&Bounds(tab).Width==(int)Math.Round(10/tabScale.M11)&&Bounds(tab).Height==(int)Math.Round(64/tabScale.M22),"handle fits work area at its own monitor DPI without size drift");
            Check(!popup.HasAnimatedProperties,"completed retract releases animation clock");
            var overTab=Center(Bounds(tab));Drive(dock,overTab,2000);Drive(dock,overTab,2299);
            Check(!popup.IsVisible,"passing over handle does not immediately pop open");
            Drive(dock,outside,2300);Drive(dock,overTab,2400);Drive(dock,overTab,2699);
            Check(!popup.IsVisible,"brief pass resets hover delay");
            Drive(dock,overTab,2700);Pump();
            Check(popup.IsVisible&&!tab.IsVisible&&!popup.HasAnimatedProperties,"300ms hover expands and completes slide");
            Check(DesktopFacts.GetForegroundWindow()!=handle,"hover expansion never takes foreground focus");
            Check(Math.Abs(Bounds(popup).Left-anchor.Left)<=1,"expanded panel returns to its verified anchor");
            Drive(dock,overTab,2800);Drive(dock,overTab,4500);
            Check(popup.IsVisible&&!Field<bool>(dock,"moving"),"reading with cursor still on handle keeps expanded panel open");
            Click(root,"SettingsButton");Pump();Drive(dock,outside,3000);Drive(dock,outside,5000);
            Check(popup.IsVisible&&!Field<bool>(dock,"moving"),"settings, note editing and dropdown interaction hold panel open");
            popup.ShowKeyEditor("fake-dock-key");((PasswordBox)root.FindName("KeyInput")).Password="fake-unsaved-key";Pump();
            Drive(dock,outside,5100);Drive(dock,outside,7100);
            Check(popup.IsVisible&&((PasswordBox)root.FindName("KeyInput")).Password=="fake-unsaved-key","key editing never collapses or loses input on mouse leave");
            Click(root,"BackButton");Click(root,"BackButton");Pump();
            popup.SetBusy(true);Drive(dock,outside,7200);Drive(dock,outside,9000);
            Check(popup.IsVisible&&!Field<bool>(dock,"moving"),"pending user operation prevents automatic collapse");
            popup.SetBusy(false);Drive(dock,outside,9100);Drive(dock,outside,9700);Pump();
            Check(!popup.IsVisible&&tab.IsVisible,"normal retract resumes after interaction completes");
            popup.ShowPopup();timer.Stop();Pump();
            Check(popup.IsVisible&&!tab.IsVisible&&Math.Abs(Bounds(popup).Left-anchor.Left)<=1,"taskbar show directly restores expanded dock panel");
            Click(root,"CloseButton");Pump();
            Check(!popup.IsVisible&&!tab.IsVisible&&!timer.IsEnabled,"X closes both panel and handle and stops timer");
            Pump(700);Check(!popup.IsVisible&&!tab.IsVisible,"closed panel does not reappear by itself");
            popup.ShowPopup();timer.Stop();Pump();
            Drive(dock,outside,10000,true);
            Check(!popup.IsVisible&&!tab.IsVisible,"fullscreen suppression hides both panel and handle");
            Drive(dock,outside,10100,false);
            Check(!popup.IsVisible&&tab.IsVisible,"leaving fullscreen restores only handle without taking focus");
            Toggle(root,false);Pump();
            Check(popup.IsVisible&&!tab.IsVisible&&!dock.Docked&&!timer.IsEnabled&&!popup.Topmost,"disabling from stowed state restores panel and original unpinned behavior");
            Check((int)Registry.GetValue(@"HKEY_CURRENT_USER\Software\CPAplusQuota","EdgeAutoHide",1)==0,"disabled preference is persisted");
            Toggle(root,true);Drag(popup,true);timer.Stop();Drag(popup,false);
            Check(!dock.Docked&&!timer.IsEnabled&&popup.IsVisible&&!popup.Topmost,"dragging away cancels docking and timer");
            popup.SetPinned(true);Drag(popup,true);timer.Stop();Drag(popup,false);
            Check(popup.Topmost,"undocking preserves an explicitly pinned popup");popup.SetPinned(false);
            foreach(bool pinned in new[]{false,true}){
                Drag(popup,false);popup.SetPinned(pinned);Pump(300);
                var growthArea=System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
                var growthScale=HwndSource.FromHwnd(handle).CompositionTarget.TransformFromDevice;
                popup.Top=growthArea.Bottom*growthScale.M22-popup.Height-12;Pump(50);
                int growthOverflow=0,growthSamples=0;double growthLeft=popup.Left;var growthAnchor=Bounds(popup);
                HwndSourceHook growing=delegate(IntPtr hwnd,int message,IntPtr w,IntPtr l,ref bool handled){
                    if(message==0x47){growthSamples++;var r=Bounds(popup);growthOverflow=Math.Max(growthOverflow,Math.Max(r.Bottom-growthArea.Bottom,growthArea.Top-r.Top));}
                    return IntPtr.Zero;
                };
                var growthSource=HwndSource.FromHwnd(handle);growthSource.AddHook(growing);
                try {
                    Click(root,"SettingsButton");Pump(350);
                    popup.ShowKeyEditor("fake-growth-key");Pump(350);
                    Click(root,"BackButton");Pump(350);
                    Click(root,"BackButton");Pump(350);
                }finally {growthSource.RemoveHook(growing);}
                Console.WriteLine("MEASURE user-positioned growth pinned="+pinned+" overflowPx="+growthOverflow+" nativeSamples="+growthSamples);
                Check(growthSamples>0&&growthOverflow==0,"all native settings/key resize frames remain inside work area; pinned="+pinned);
                Check(Math.Abs(popup.Left-growthLeft)<0.5&&!dock.Docked,"resize preserves manual horizontal position without docking; pinned="+pinned);
                Console.WriteLine("MEASURE settings return pinned="+pinned+" before="+growthAnchor+" after="+Bounds(popup));
                Check(Math.Abs(Bounds(popup).Top-growthAnchor.Top)<=1,"return from taller pages restores original manual top; pinned="+pinned);
                Click(root,"SettingsButton");Pump(350);Click(root,"CloseButton");Pump(50);popup.ShowPopup();Pump(350);
                Check(Math.Abs(Bounds(popup).Top-growthAnchor.Top)<=1,"closing taller page then reopening home restores original top; pinned="+pinned);
                Click(root,"SettingsButton");Pump(70);Click(root,"BackButton");Pump(350);
                Check(Math.Abs(Bounds(popup).Top-growthAnchor.Top)<=1,"reversing page growth mid-animation restores original top; pinned="+pinned);
            }
            popup.SetPinned(false);
            Drag(popup,true);timer.Stop();anchor=Bounds(popup);
            popup.Top=work.Bottom*HwndSource.FromHwnd(handle).CompositionTarget.TransformFromDevice.M22-popup.Height-12;
            int largestOverflow=0;var source=HwndSource.FromHwnd(handle);
            HwndSourceHook sized=delegate(IntPtr hwnd,int message,IntPtr w,IntPtr l,ref bool handled){if(message==0x47)largestOverflow=Math.Max(largestOverflow,Bounds(popup).Bottom-work.Bottom);return IntPtr.Zero;};
            source.AddHook(sized);
            try {Click(root,"SettingsButton");Pump(300);Click(root,"BackButton");Pump(300);}
            finally {source.RemoveHook(sized);}
            Check(largestOverflow==0&&Bounds(popup).Bottom<=work.Bottom,"dock content expansion never paints below work area in native size frames");
            anchor=Bounds(popup);
            var process=Process.GetCurrentProcess();TimeSpan cpu=process.TotalProcessorTime;var watch=Stopwatch.StartNew();
            for(int i=0;i<2000;i++)Drive(dock,Center(anchor),11000+i*150,DesktopMonitor.Fullscreen(anchor));
            watch.Stop();process.Refresh();Console.WriteLine("MEASURE 2000 dock ticks wallMs="+watch.Elapsed.TotalMilliseconds.ToString("F2")+" cpuMs="+(process.TotalProcessorTime-cpu).TotalMilliseconds.ToString("F2"));
            Check(!Field<bool>(dock,"moving")&&popup.IsVisible,"steady pointer checks do not animate or rebuild panel");
            outside=new Point(anchor.Left-100,anchor.Top+10);
            Drive(dock,outside,400000);Drive(dock,outside,400600);
            Check(Field<bool>(dock,"moving"),"retract is in flight for cancellation test");
            Click(root,"CloseButton");Pump();
            Console.WriteLine("MEASURE interrupted close popup="+popup.IsVisible+" tab="+tab.IsVisible+" timer="+timer.IsEnabled+" stowed="+Field<bool>(dock,"stowed")+" moving="+Field<bool>(dock,"moving"));
            Check(!popup.IsVisible&&!tab.IsVisible&&!timer.IsEnabled,"X during retract cancels animation without a resurrected handle");
            popup.ShowPopup();timer.Stop();Pump();Drive(dock,outside,401000);Drive(dock,outside,401600);
            popup.ShowPopup();timer.Stop();Pump();
            Check(popup.IsVisible&&!tab.IsVisible&&!popup.HasAnimatedProperties,"taskbar activation during retract cancels motion and stays expanded");
            Drive(dock,outside,402000);Drive(dock,outside,402600);Drag(popup,false);Pump();
            Check(popup.IsVisible&&!dock.Docked&&!tab.IsVisible&&!timer.IsEnabled,"dragging during retract cancels late collapse completion");
            Drag(popup,true);timer.Stop();Drive(dock,outside,403000);Drive(dock,outside,403600);
            popup.Shutdown();popup=null;
            Check(!timer.IsEnabled&&!tab.IsVisible,"shutdown disposes timer and edge handle");
            Console.WriteLine("TOTAL "+checks+" checks PASS; isolated fake settings/accounts; no HTTP or credential reads");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally {if(popup!=null)popup.Shutdown();RegOverridePredefKey(new IntPtr(unchecked((int)0x80000001)),IntPtr.Zero);if(hive!=null)hive.Dispose();Registry.CurrentUser.DeleteSubKeyTree(registry,false);}
    }
}
