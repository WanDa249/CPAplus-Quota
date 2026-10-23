using System;
using System.Diagnostics;
using Rectangle=System.Drawing.Rectangle;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

// Optional popup behavior, independent of quota reads and taskbar visibility policy.
internal sealed class PopupEdgeDock : IDisposable
{
    private readonly Window popup;
    private readonly Func<bool> holdOpen,pinned;
    private readonly Brush accent;
    private readonly DispatcherTimer timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(150)};
    private readonly Stopwatch clock=Stopwatch.StartNew();
    private Window tab;
    private Rectangle area;
    private string device;
    private int side;
    private int motionVersion;
    private double expandedLeft;
    private bool stowed,moving,disposed;
    private long hoverSince=-1,awaySince=-1,nextEnvironmentCheck;
    internal bool Enabled {get;private set;}
    internal bool Docked {get{return side!=0;}}

    internal PopupEdgeDock(Window window,Func<bool> hold,Func<bool> isPinned,Brush color)
    {
        popup=window;holdOpen=hold;pinned=isPinned;accent=color;
        Enabled=object.Equals(Registry.GetValue(@"HKEY_CURRENT_USER\Software\CPAplusQuota","EdgeAutoHide",0),1);
        timer.Tick+=delegate {Advance(System.Windows.Forms.Cursor.Position,clock.ElapsedMilliseconds,DesktopMonitor.Fullscreen(area));};
        popup.IsVisibleChanged+=VisibilityChanged;
        SystemEvents.DisplaySettingsChanged+=DisplayChanged;
    }
    internal void SetEnabled(bool value)
    {
        using(var key=Registry.CurrentUser.CreateSubKey(@"Software\CPAplusQuota"))key.SetValue("EdgeAutoHide",value?1:0,RegistryValueKind.DWord);
        Enabled=value;
        if(!value){bool show=stowed;PrepareShow();BeginMove();if(show)ShowWithoutActivation();}
    }
    // Only a completed user drag arms docking. Enabling the option alone never moves a window.
    internal void BeginMove()
    {
        CancelMotion();timer.Stop();if(tab!=null)tab.Hide();
        side=0;stowed=false;popup.Topmost=pinned();ResetDelay();
    }
    internal static int ChooseSide(Rectangle panel,Rectangle work,Rectangle monitor,Rectangle[] screens,int threshold)
    {
        int left=Math.Abs(panel.Left-work.Left),right=Math.Abs(panel.Right-work.Right);
        int result=left<=threshold&&left<=right?-1:right<=threshold?1:0;
        int x=result<0?monitor.Left-1:monitor.Right;
        // A shared monitor seam is not a hiding place: the panel would slide onto another screen.
        if(result!=0&&screens.Any(s=>s!=monitor&&x>=s.Left&&x<s.Right&&panel.Top<s.Bottom&&panel.Bottom>s.Top))return 0;
        return result;
    }
    internal static Rectangle SnapBounds(Rectangle panel,Rectangle work,Rectangle monitor,Rectangle[] screens,int margin,int threshold)
    {
        var target=Rectangle.Inflate(work,-margin,-margin);
        int horizontal=panel.Width<=target.Width?ChooseSide(panel,target,monitor,screens,threshold):0;
        if(horizontal!=0)panel.X=horizontal<0?target.Left:target.Right-panel.Width;
        int top=Math.Abs(panel.Top-target.Top),bottom=Math.Abs(panel.Bottom-target.Bottom);
        int vertical=top<=threshold&&top<=bottom?-1:bottom<=threshold?1:0;
        int y=vertical<0?monitor.Top-1:monitor.Bottom;
        bool shared=screens.Any(s=>s!=monitor&&y>=s.Top&&y<s.Bottom&&panel.Left<s.Right&&panel.Right>s.Left);
        if(vertical!=0&&!shared&&panel.Height<=target.Height)panel.Y=vertical<0?target.Top:target.Bottom-panel.Height;
        // On release, containment is unconditional, including overshoot beyond the snap radius.
        var horizontalArea=panel.Width<=target.Width?target:work;
        var verticalArea=panel.Height<=target.Height?target:work;
        if(panel.Width<=work.Width&&(panel.Left<work.Left||panel.Right>work.Right))panel.X=Math.Max(horizontalArea.Left,Math.Min(panel.X,horizontalArea.Right-panel.Width));
        if(panel.Height<=work.Height&&(panel.Top<work.Top||panel.Bottom>work.Bottom))panel.Y=Math.Max(verticalArea.Top,Math.Min(panel.Y,verticalArea.Bottom-panel.Height));
        return panel;
    }
    internal void EndMove()
    {
        if(!popup.IsVisible)return;
        var handle=new WindowInteropHelper(popup).Handle;DesktopFacts.Rect bounds;
        if(!DesktopFacts.GetWindowRect(handle,out bounds))return;
        var screen=System.Windows.Forms.Screen.FromHandle(handle);var scale=Scale();
        var screens=System.Windows.Forms.Screen.AllScreens.Select(s=>s.Bounds).ToArray();
        // Never alter WM_MOVING: keep both axes free until the native drag has ended.
        var snapped=SnapBounds(bounds.Rectangle,screen.WorkingArea,screen.Bounds,screens,(int)Math.Ceiling(12/scale.M11),(int)Math.Ceiling(18/scale.M11));
        if(snapped!=bounds.Rectangle){popup.Left=snapped.Left*scale.M11;popup.Top=snapped.Top*scale.M22;}
        if(!Enabled)return;
        side=ChooseSide(snapped,screen.WorkingArea,screen.Bounds,screens,(int)Math.Ceiling(16/scale.M11));
        if(side==0)return;
        area=screen.WorkingArea;device=screen.DeviceName;
        expandedLeft=side<0?area.Left*scale.M11+12:area.Right*scale.M11-popup.Width-12;
        popup.Left=expandedLeft;
        popup.Top=Math.Max(area.Top*scale.M22+12,Math.Min(popup.Top,area.Bottom*scale.M22-popup.Height-12));
        popup.Topmost=true;ResetDelay();nextEnvironmentCheck=0;timer.Start();
    }
    private Matrix Scale(){return HwndSource.FromHwnd(new WindowInteropHelper(popup).Handle).CompositionTarget.TransformFromDevice;}
    internal int ClampTop(int top,int height)
    {
        int margin=(int)Math.Ceiling(12/Scale().M22);
        return Math.Max(area.Top+margin,Math.Min(top,area.Bottom-height-margin));
    }
    private void ResetDelay(){hoverSince=-1;awaySince=-1;}
    private void CancelMotion()
    {
        motionVersion++;double current=popup.Left;popup.BeginAnimation(Window.LeftProperty,null);popup.Left=current;moving=false;
    }
    internal void PrepareShow()
    {
        CancelMotion();if(side!=0)popup.Left=expandedLeft;
        stowed=false;if(tab!=null)tab.Hide();ResetDelay();
    }
    internal void Shown(){if(Enabled&&side!=0){popup.Topmost=true;timer.Start();}}
    private void VisibilityChanged(object sender,DependencyPropertyChangedEventArgs e)
    {
        if(!popup.IsVisible&&!stowed){PrepareShow();timer.Stop();}
    }
    private void DisplayChanged(object sender,EventArgs e)
    {
        popup.Dispatcher.BeginInvoke(new Action(delegate {if(!disposed&&side!=0){PrepareShow();BeginMove();popup.Hide();}}));
    }
    private void ShowWithoutActivation()
    {
        bool activate=popup.ShowActivated;popup.ShowActivated=false;
        try {popup.Show();}finally {popup.ShowActivated=activate;}
    }
    private void Animate(double target,Action completed)
    {
        moving=true;int version=++motionVersion;
        var animation=new DoubleAnimation(popup.Left,target,TimeSpan.FromMilliseconds(180)) {EasingFunction=new SineEase {EasingMode=EasingMode.EaseInOut}};
        animation.Completed+=delegate {if(disposed||version!=motionVersion)return;popup.Left=target;popup.BeginAnimation(Window.LeftProperty,null);moving=false;completed();};
        popup.BeginAnimation(Window.LeftProperty,animation);
    }
    private void ShowTab()
    {
        var scale=Scale();
        if(tab==null){
            var pill=new System.Windows.Controls.Border {Background=accent,Opacity=0.7,CornerRadius=new CornerRadius(4),Margin=new Thickness(2,8,2,8)};
            var hit=new Grid {Background=Brushes.Transparent,ToolTip="额度 · 悬停展开"};hit.Children.Add(pill);
            tab=new Window {Title="CPAplus 额度 · 贴边",Width=10,Height=64,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,ShowInTaskbar=false,ShowActivated=false,Topmost=true,Content=hit};
            tab.MouseLeftButtonDown+=delegate {Expand(true);};
        }
        tab.Left=side<0?area.Left*scale.M11:area.Right*scale.M11-tab.Width;
        tab.Top=Math.Max(area.Top*scale.M22,Math.Min(popup.Top+24,area.Bottom*scale.M22-tab.Height));
        tab.Show();
    }
    private void Stow(bool animate)
    {
        if(animate){
            var scale=Scale();double hidden=side<0?area.Left*scale.M11-popup.Width:area.Right*scale.M11;
            Animate(hidden,delegate {stowed=true;popup.Hide();ShowTab();ResetDelay();});
        }else {CancelMotion();stowed=true;popup.Hide();if(tab!=null)tab.Hide();ResetDelay();}
    }
    private void Expand(bool activate)
    {
        if(disposed||!stowed)return;
        if(tab!=null)tab.Hide();stowed=false;ResetDelay();
        // Retain the offscreen start of a normal slide, but not a fullscreen suppression.
        var scale=Scale();popup.Left=side<0?area.Left*scale.M11-popup.Width:area.Right*scale.M11;
        ShowWithoutActivation();Animate(expandedLeft,delegate {});
        if(activate)popup.Activate();
    }
    internal void Advance(System.Drawing.Point cursor,long now,bool fullscreen)
    {
        if(disposed||!Enabled||side==0||!timer.IsEnabled)return;
        if(now>=nextEnvironmentCheck){
            nextEnvironmentCheck=now+1000;
            if(!System.Windows.Forms.Screen.AllScreens.Any(s=>s.DeviceName==device&&s.WorkingArea==area)){PrepareShow();BeginMove();popup.Hide();return;}
        }
        if(fullscreen){Stow(false);return;}
        if(moving)return;
        if(stowed){
            if(tab==null||!tab.IsVisible)ShowTab();
            DesktopFacts.Rect bounds;
            bool over=DesktopFacts.GetWindowRect(new WindowInteropHelper(tab).Handle,out bounds)&&bounds.Rectangle.Contains(cursor);
            if(!over)hoverSince=-1;
            else if(hoverSince<0)hoverSince=now;
            else if(now-hoverSince>=300)Expand(false);
            return;
        }
        DesktopFacts.Rect rect;
        bool inside=DesktopFacts.GetWindowRect(new WindowInteropHelper(popup).Handle,out rect)&&rect.Rectangle.Contains(cursor);
        DesktopFacts.Rect handleBounds;
        if(!inside&&tab!=null&&DesktopFacts.GetWindowRect(new WindowInteropHelper(tab).Handle,out handleBounds))inside=Rectangle.Union(rect.Rectangle,handleBounds.Rectangle).Contains(cursor);
        if(inside||holdOpen()||Mouse.LeftButton==MouseButtonState.Pressed){awaySince=-1;return;}
        if(awaySince<0)awaySince=now;
        else if(now-awaySince>=600)Stow(true);
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;timer.Stop();CancelMotion();
        popup.IsVisibleChanged-=VisibilityChanged;SystemEvents.DisplaySettingsChanged-=DisplayChanged;
        if(tab!=null)tab.Close();
    }
}
