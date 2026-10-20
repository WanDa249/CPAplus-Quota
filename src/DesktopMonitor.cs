using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;

internal sealed class DesktopMonitor : IDisposable
{
    private readonly TaskbarStrip strip;
    private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer {Interval=1000};
    private readonly System.Windows.Forms.Timer eventTimer=new System.Windows.Forms.Timer {Interval=100};
    private readonly System.Windows.Forms.Timer layerTimer=new System.Windows.Forms.Timer {Interval=33};
    private DateTime layerUntil;
    private readonly uint ownPid=(uint)Process.GetCurrentProcess().Id;
    private readonly DesktopFacts.WinEvent callback;
    private readonly List<IntPtr> hooks=new List<IntPtr>();
    private IntPtr taskbar;
    private IntPtr locationHook;
    private IntPtr reorderHook;
    private List<DesktopFacts.ButtonRect> buttons;
    private Rectangle measuredBar,measuredTray;
    private bool measuring;
    private bool geometryDirty=true;
    private volatile bool disposed;
    private double probeMs;
    private string previousState;
    private DateTime measuredAt;
    private int geometryVersion;
    private long reconcileCount,windowEvents,reorderEvents,probeCount;
    private DateTime recordedAt;
    private List<DesktopFacts.CodexWindow> codexGui;
    private VisibilityMode mode=LoadMode();
    internal VisibilityMode Mode
    {
        get{return mode;}
        set {
            if(!Enum.IsDefined(typeof(VisibilityMode),value))throw new ArgumentOutOfRangeException("value");
            if(mode==value)return;
            using(var key=Registry.CurrentUser.CreateSubKey(@"Software\CPAplusQuota"))key.SetValue("DisplayMode",(int)value,RegistryValueKind.DWord);
            mode=value;
        }
    }
    private static VisibilityMode LoadMode()
    {
        using(var key=Registry.CurrentUser.OpenSubKey(@"Software\CPAplusQuota")){
            int value;return key!=null&&int.TryParse(Convert.ToString(key.GetValue("DisplayMode")),out value)&&Enum.IsDefined(typeof(VisibilityMode),value)?(VisibilityMode)value:VisibilityMode.CodexRunning;
        }
    }
    internal static string VisibilityReason(VisibilityMode mode,IList<DesktopFacts.CodexWindow> windows,bool foreground)
    {
        if(mode==VisibilityMode.Hidden)return "user-hidden";
        if(mode==VisibilityMode.Always)return null;
        if(windows.Count==0)return "codex-closed";
        if(mode==VisibilityMode.CodexForeground&&!foreground)return "codex-not-foreground";
        if(mode==VisibilityMode.CodexNotMinimized&&windows.All(window=>window.minimized))return "codex-minimized";
        return null;
    }
    internal bool CodexRunning {get;private set;}
    internal event Action<bool> CodexRunningChanged;
    internal static string EvidenceDirectory;

    internal DesktopMonitor(TaskbarStrip window)
    {
        strip=window;callback=OnWindowEvent;
        hooks.Add(DesktopFacts.SetWinEventHook(3,3,IntPtr.Zero,callback,0,0,2));
        hooks.Add(DesktopFacts.SetWinEventHook(0x16,0x17,IntPtr.Zero,callback,0,0,2));
        hooks.Add(DesktopFacts.SetWinEventHook(0x8000,0x8003,IntPtr.Zero,callback,0,0,2));
        eventTimer.Tick+=delegate {eventTimer.Stop();Reconcile();};
        layerTimer.Tick+=delegate {if(DateTime.UtcNow>=layerUntil){layerTimer.Stop();return;}strip.RestoreLayer();};
        timer.Tick+=delegate { Reconcile(); };timer.Start();Reconcile();
    }
    private void OnWindowEvent(IntPtr hook,uint type,IntPtr hwnd,int obj,int child,uint thread,uint time)
    {
        if(disposed)return;
        if(hook==reorderHook){
            if(DesktopFacts.GetAncestor(hwnd,2)!=taskbar)return;
            reorderEvents++;InvalidateGeometry(true);eventTimer.Start();return;
        }
        if(obj!=0||child!=0)return;
        windowEvents++;
        uint pid;DesktopFacts.GetWindowThreadProcessId(hwnd,out pid);
        if(type!=3&&pid==ownPid)return;
        if(type==3||type==0x16||type==0x17){
            Reconcile();
            if(strip.Visible){strip.RestoreLayer();layerUntil=DateTime.UtcNow.AddMilliseconds(600);layerTimer.Start();}
            return;
        }
        if(type!=3) {
            bool taskWindow=DesktopFacts.IsWindowVisible(hwnd)&&DesktopFacts.AppWindow(hwnd);
            if(hwnd!=taskbar&&!taskWindow)return;
            bool existingCodex=codexGui!=null&&codexGui.Any(w=>w.hwnd==hwnd.ToInt64());
            InvalidateGeometry(!existingCodex);
        }
        // Out-of-context hooks run on the installing message-loop thread.
        eventTimer.Start();
    }
    private bool Fullscreen(Rectangle bar)
    {
        IntPtr front=DesktopFacts.GetForegroundWindow();string cls=DesktopFacts.Class(front);
        if(front==IntPtr.Zero||cls=="Progman"||cls=="WorkerW"||cls=="Shell_TrayWnd"||cls=="Shell_SecondaryTrayWnd"||DesktopFacts.IsIconic(front)||DesktopFacts.Cloaked(front))return false;
        DesktopFacts.Rect r;if(!DesktopFacts.GetWindowRect(front,out r))return false;
        var screen=Screen.FromRectangle(bar);if(Screen.FromHandle(front).DeviceName!=screen.DeviceName)return false;
        return r.Left<=screen.Bounds.Left+2&&r.Top<=screen.Bounds.Top+2&&r.Right>=screen.Bounds.Right-2&&r.Bottom>=screen.Bounds.Bottom-2;
    }

    internal void Reconcile()
    {
        if(disposed)return;
        reconcileCount++;
        bool codexForeground;var codex=DesktopFacts.CodexWindows(out codexForeground);
        codexGui=codex;
        bool running=codex.Count>0;
        timer.Interval=running||Mode==VisibilityMode.Always?1000:5000;
        if(CodexRunning!=running){CodexRunning=running;if(CodexRunningChanged!=null)CodexRunningChanged(running);}
        string visibilityReason=VisibilityReason(Mode,codex,codexForeground);
        if(visibilityReason!=null){
            layerTimer.Stop();strip.Hide();Record(visibilityReason,codex.Count,codexForeground,Rectangle.Empty,Rectangle.Empty,0);return;
        }
        IntPtr current=DesktopFacts.FindWindow("Shell_TrayWnd",null);
        DesktopFacts.Rect barRect,trayRect;
        IntPtr tray=DesktopFacts.FindWindowEx(current,IntPtr.Zero,"TrayNotifyWnd",null);
        bool ready=current!=IntPtr.Zero&&DesktopFacts.IsWindowVisible(current)&&DesktopFacts.GetWindowRect(current,out barRect);
        DesktopFacts.GetWindowRect(current,out barRect);DesktopFacts.GetWindowRect(tray,out trayRect);
        if(current!=taskbar){
            if(locationHook!=IntPtr.Zero)DesktopFacts.UnhookWinEvent(locationHook);
            if(reorderHook!=IntPtr.Zero)DesktopFacts.UnhookWinEvent(reorderHook);
            taskbar=current;InvalidateGeometry(true);uint explorer;DesktopFacts.GetWindowThreadProcessId(current,out explorer);
            locationHook=explorer==0?IntPtr.Zero:DesktopFacts.SetWinEventHook(0x800B,0x800B,IntPtr.Zero,callback,explorer,0,2);
            reorderHook=explorer==0?IntPtr.Zero:DesktopFacts.SetWinEventHook(0x8004,0x8004,IntPtr.Zero,callback,explorer,0,2);
        }
        string reason=!ready||tray==IntPtr.Zero?"taskbar-unavailable":barRect.Rectangle.Height>barRect.Rectangle.Width?"vertical-taskbar-unsupported":!Screen.FromRectangle(barRect.Rectangle).Bounds.Contains(barRect.Rectangle)?"taskbar-hidden":Fullscreen(barRect.Rectangle)?"fullscreen":null;
        if(reason!=null){strip.Hide();Record(reason,codex.Count,codexForeground,barRect.Rectangle,trayRect.Rectangle,0);return;}
        if(measuredBar!=barRect.Rectangle||measuredTray!=trayRect.Rectangle){if(buttons!=null)InvalidateGeometry(true);}
        if(!measuring&&(geometryDirty||buttons==null||(DateTime.UtcNow-measuredAt).TotalSeconds>=5)){Measure(current,barRect.Rectangle,trayRect.Rectangle);}
        if(buttons==null){strip.Hide();Record("measuring-space",codex.Count,codexForeground,barRect.Rectangle,trayRect.Rectangle,0);return;}
        // Require positive task-button evidence; a failed/incomplete UIA read never grants space.
        var occupied=buttons.Where(b=>b.x+b.width<=trayRect.Left+2&&b.width>0&&b.x< trayRect.Left-2).ToArray();
        if(occupied.Length==0){strip.Hide();Record("space-unknown",codex.Count,codexForeground,barRect.Rectangle,trayRect.Rectangle,0);return;}
        int left=(int)Math.Ceiling(occupied.Max(b=>b.x+b.width));
        int trayLeft=Math.Min(trayRect.Left,(int)Math.Floor(buttons.Where(b=>b.id=="SystemTrayIcon"||b.id=="NotifyItemIcon").Select(b=>b.x).DefaultIfEmpty(trayRect.Left).Min()));
        bool shown=strip.Present(barRect.Rectangle,left,trayLeft,(int)DesktopFacts.GetDpiForWindow(current));
        Record(shown?"shown":"no-space",codex.Count,codexForeground,barRect.Rectangle,trayRect.Rectangle,left);
    }

    private async void Measure(IntPtr bar,Rectangle br,Rectangle tr)
    {
        measuring=true;probeCount++;var watch=Stopwatch.StartNew();int version=geometryVersion;
        List<DesktopFacts.ButtonRect> found=null;
        try {found=await Task.Run(()=>DesktopFacts.ButtonFacts(bar));}
        catch(ElementNotAvailableException){}
        catch(System.Runtime.InteropServices.COMException){}
        catch(InvalidOperationException){}
        finally{probeMs=watch.Elapsed.TotalMilliseconds;measuring=false;}
        if(disposed||taskbar!=bar||version!=geometryVersion)return;
        buttons=found;geometryDirty=false;measuredBar=br;measuredTray=tr;measuredAt=DateTime.UtcNow;
        if(found!=null&&found.Count>0)Reconcile();else strip.Hide();
    }

    private void Record(string reason,int codex,bool foreground,Rectangle bar,Rectangle tray,int occupiedRight)
    {
        if(EvidenceDirectory==null)return;
        string signature=reason+"|"+Mode+"|"+codex+"|"+foreground+"|"+bar+"|"+tray+"|"+strip.Bounds+"|"+strip.CurrentText+"|"+codexGui.Any(w=>w.minimized);
        if(signature==previousState&&(DateTime.UtcNow-recordedAt).TotalSeconds<1)return;
        recordedAt=DateTime.UtcNow;
        var state=new {time=DateTimeOffset.Now.ToString("o"),reason=reason,mode=Mode.ToString(),codexWindows=codex,codexGui=codexGui,codexForeground=foreground,taskbar=bar,tray=tray,occupiedRight=occupiedRight,overlay=strip.Bounds,overlayVisible=strip.Visible,text=strip.CurrentText,alias=strip.SelectedAlias,probeMs=probeMs,foregroundHwnd=DesktopFacts.GetForegroundWindow().ToInt64(),reconcileCount=reconcileCount,windowEvents=windowEvents,reorderEvents=reorderEvents,probeCount=probeCount};
        string json=new JavaScriptSerializer().Serialize(state);
        File.WriteAllText(Path.Combine(EvidenceDirectory,"state.json"),json);
        if(signature!=previousState){File.AppendAllText(Path.Combine(EvidenceDirectory,"transitions.jsonl"),json+Environment.NewLine);previousState=signature;}
    }
    private void InvalidateGeometry(bool boundsChanged=false)
    {
        geometryVersion++;geometryDirty=true;
        // Preserve a validated corridor through focus/minimize events. Four button widths
        // provide room while the background probe completes; a tight corridor fails closed.
        double occupied=buttons==null?double.MaxValue:buttons.Where(b=>b.x+b.width<=measuredTray.Left&&b.x<measuredTray.Left).Select(b=>b.x+b.width).DefaultIfEmpty(double.MaxValue).Max();
        int dpi=taskbar==IntPtr.Zero?96:(int)DesktopFacts.GetDpiForWindow(taskbar);
        if(boundsChanged||strip.Left-occupied<256*dpi/96.0){buttons=null;strip.Hide();}
    }
    public void Dispose(){if(disposed)return;disposed=true;timer.Stop();timer.Dispose();eventTimer.Dispose();layerTimer.Dispose();if(locationHook!=IntPtr.Zero)DesktopFacts.UnhookWinEvent(locationHook);if(reorderHook!=IntPtr.Zero)DesktopFacts.UnhookWinEvent(reorderHook);foreach(var hook in hooks)if(hook!=IntPtr.Zero)DesktopFacts.UnhookWinEvent(hook);}
}
