using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

internal static class DesktopFacts
{
    internal sealed class ButtonRect { public string id; public double x,y,width,height; }
    internal sealed class CodexWindow { public long hwnd;public uint pid;public bool minimized,cloaked;public string windowClass; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left,Top,Right,Bottom; public Rectangle Rectangle { get { return Rectangle.FromLTRB(Left,Top,Right,Bottom); } } }
    internal delegate bool EnumProc(IntPtr hwnd,IntPtr parameter);
    internal delegate void WinEvent(IntPtr hook,uint type,IntPtr hwnd,int objectId,int childId,uint thread,uint time);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string cls,string title);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback,IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd,uint command);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] internal static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd,StringBuilder text,int size);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,WinEvent callback,uint pid,uint thread,uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd,int attribute,out int value,int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern int GetPackageFamilyName(IntPtr process,ref uint size,StringBuilder name);

    internal static string Class(IntPtr hwnd) { var s=new StringBuilder(256);GetClassName(hwnd,s,s.Capacity);return s.ToString(); }
    internal static bool Cloaked(IntPtr hwnd) { int value;return DwmGetWindowAttribute(hwnd,14,out value,4)==0 && value!=0; }
    internal static bool AppWindow(IntPtr hwnd)
    {
        int style=GetWindowLong(hwnd,-16),extended=GetWindowLong(hwnd,-20);
        return GetAncestor(hwnd,2)==hwnd&&GetWindow(hwnd,4)==IntPtr.Zero&&(extended&0x80)==0&&((style&0xC40000)!=0||(extended&0x40000)!=0);
    }
    internal static bool CodexProcess(uint pid)
    {
        IntPtr process=OpenProcess(0x1000,false,pid);if(process==IntPtr.Zero)return false;
        try { uint length=0;GetPackageFamilyName(process,ref length,null);if(length==0)return false;var name=new StringBuilder((int)length);return GetPackageFamilyName(process,ref length,name)==0 && name.ToString()=="OpenAI.Codex_2p2nqsd0c76g0"; }
        finally { CloseHandle(process); }
    }

    internal static List<CodexWindow> CodexWindows(out bool foreground)
    {
        var windows=new List<CodexWindow>();var pids=new HashSet<uint>();
        EnumWindows(delegate(IntPtr hwnd,IntPtr ignored){
            if((!IsWindowVisible(hwnd)&&!IsIconic(hwnd))||!AppWindow(hwnd)||!Class(hwnd).StartsWith("Chrome_WidgetWin_1",StringComparison.Ordinal))return true;
            uint pid;GetWindowThreadProcessId(hwnd,out pid);if(!CodexProcess(pid))return true;
            pids.Add(pid);windows.Add(new CodexWindow { hwnd=hwnd.ToInt64(),pid=pid,minimized=IsIconic(hwnd),cloaked=Cloaked(hwnd),windowClass=Class(hwnd) });return true;
        },IntPtr.Zero);
        uint frontPid;GetWindowThreadProcessId(GetForegroundWindow(),out frontPid);foreground=pids.Contains(frontPid);
        return windows;
    }

    internal static List<ButtonRect> ButtonFacts(IntPtr taskbar)
    {
        var buttons=new List<ButtonRect>();
        var request=new CacheRequest();request.Add(AutomationElement.BoundingRectangleProperty);request.Add(AutomationElement.AutomationIdProperty);request.Add(AutomationElement.IsOffscreenProperty);
        request.AutomationElementMode=AutomationElementMode.None;
        var root=AutomationElement.FromHandle(taskbar);
        using(request.Activate()) {
            var elements=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button));
            foreach(AutomationElement e in elements){var r=e.Cached.BoundingRectangle;if(e.Cached.IsOffscreen||r.IsEmpty||r.Width<1)continue;buttons.Add(new ButtonRect {id=e.Cached.AutomationId,x=r.X,y=r.Y,width=r.Width,height=r.Height});}
        }
        return buttons;
    }

    internal static object Diagnose()
    {
        IntPtr bar=FindWindow("Shell_TrayWnd",null),tray=FindWindowEx(bar,IntPtr.Zero,"TrayNotifyWnd",null);
        Rect br,tr;GetWindowRect(bar,out br);GetWindowRect(tray,out tr);bool foreground;
        return new {taskbar=br.Rectangle,tray=tr.Rectangle,dpi=bar==IntPtr.Zero?0:GetDpiForWindow(bar),codexWindows=CodexWindows(out foreground),codexForeground=foreground,buttons=ButtonFacts(bar)};
    }
}
