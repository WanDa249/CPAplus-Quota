using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal enum VisibilityMode { CodexRunning, CodexForeground, Always, CodexNotMinimized, Hidden }

internal sealed class TaskbarStrip : Form
{
    internal const TextRenderingHint TextHint=TextRenderingHint.AntiAlias;
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X,Y; public Point(int x,int y){X=x;Y=y;} }
    [StructLayout(LayoutKind.Sequential)] private struct LayerSize { public int Width,Height; public LayerSize(int w,int h){Width=w;Height=h;} }
    [StructLayout(LayoutKind.Sequential,Pack=1)] private struct Blend { public byte Operation,Flags,Alpha,Format; }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd,IntPtr screen,ref Point destination,ref LayerSize size,IntPtr source,ref Point origin,uint color,ref Blend blend,uint flags);
    private readonly ToolTip tooltip=new ToolTip { AutoPopDelay=25000,InitialDelay=500,ReshowDelay=100 };
    internal event EventHandler DetailsRequested;
    internal event Action<string> AccountSelected;
    private QuotaResult data;
    private string error="正在查询额度…",selected;
    private Rectangle lastBounds;
    private string lastText;
    private string paintedText,paintedAlias;
    private readonly Timer transition=new Timer {Interval=25};
    private readonly Stopwatch transitionClock=new Stopwatch();
    private Bitmap transitionFrame;
    private Rectangle transitionBounds;
    internal string CurrentText {get;private set;}
    internal QuotaResult CurrentData {get{return data;}}
    internal string SelectedAlias { get { var a=Account();return a==null?null:a.alias; } }

    internal TaskbarStrip()
    {
        Text="CPAplus 任务栏额度实验";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;
        AutoScaleMode=AutoScaleMode.None;
        MouseUp+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left&&DetailsRequested!=null)DetailsRequested(this,EventArgs.Empty);};
        transition.Tick+=delegate {
            if(!Visible){StopTransition();lastText=null;return;}
            byte alpha=TransitionAlpha(transitionClock.Elapsed.TotalMilliseconds);
            PaintLayer(transitionFrame,transitionBounds,alpha);
            if(alpha==255)StopTransition();
        };
        var handle=Handle;
    }
    protected override bool ShowWithoutActivation {get{return true;}}
    protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x80000|0x8000000|0x80;return p;}}
    protected override void WndProc(ref Message m){if(m.Msg==0x21){m.Result=new IntPtr(3);return;}base.WndProc(ref m);}
    internal void RestoreLayer(){if(Visible)DesktopFacts.SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x1|0x2|0x10|0x200);}

    internal void UpdateData(QuotaResult next,string failure)
    {
        string previous=selected;
        data=next;error=failure;
        var a=Account();
        if(a==null){tooltip.SetToolTip(this,failure??"当前无绑定账号");return;}
        selected=a.alias;
        if(previous!=selected&&AccountSelected!=null)AccountSelected(selected);
        var text=new StringBuilder(AccountNames.Label(a)+"\n");
        text.Append(a.frozen?"已冻结":a.disabled?"已停用":a.available?"可用":"当前不可用");
        if(a.stale)text.Append(" · 额度信息已过期");
        if(a.cacheStatus=="error")text.Append(" · 最近查询失败");
        text.Append("\n五小时重置 "+Theme.Time(a.fiveHour==null?null:a.fiveHour.resetAt));
        text.Append("\n周额度重置 "+Theme.Time(a.weekly==null?null:a.weekly.resetAt));
        text.Append("\n最近成功 "+Theme.Time(a.lastSuccessAt)+"\n点击查看全部绑定账号；滚轮切换显示账号。");
        text.Append("\n颜色：高于50%绿，高于20%至50%黄，20%及以下红；小圆点表示状态需注意。");
        tooltip.SetToolTip(this,text.ToString());lastText=null;
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);if(data==null||data.accounts.Count<2)return;
        int index=data.accounts.FindIndex(a=>a.alias==selected);index=(index+(e.Delta<0?1:data.accounts.Count-1))%data.accounts.Count;
        SelectAccount(data.accounts[index].alias);
    }
    internal void SelectAccount(string alias)
    {
        if(data==null||!data.accounts.Any(a=>a.alias==alias))return;
        selected=alias;UpdateData(data,error);if(AccountSelected!=null)AccountSelected(alias);
    }
    internal void RefreshNames(){UpdateData(data,error);}
    private QuotaAccount Account()
    {
        if(data==null)return null;
        return data.accounts.FirstOrDefault(a=>a.alias==selected)??data.accounts.FirstOrDefault(a=>a.supported&&a.available&&!a.stale)??data.accounts.FirstOrDefault(a=>a.supported&&a.available)??data.accounts.FirstOrDefault();
    }
    private static string Percent(QuotaWindow w){return w==null||!w.remainingPercent.HasValue?"—":w.remainingPercent.Value.ToString("0")+"%";}
    // Fixed prototype defaults; no flashing or animated warning colors.
    internal static Color QuotaColor(double? remaining,bool light)
    {
        if(!remaining.HasValue)return light?Color.FromArgb(82,92,98):Color.FromArgb(190,198,202);
        double displayed=Math.Round(remaining.Value);
        if(displayed<=20)return light?Color.FromArgb(163,62,59):Color.FromArgb(220,150,147);
        if(displayed<=50)return light?Color.FromArgb(138,92,16):Color.FromArgb(216,187,122);
        return light?Color.FromArgb(35,104,71):Color.FromArgb(139,205,171);
    }

    internal static Rectangle Place(Rectangle bar,int freeLeft,int trayLeft,int required,int dpi)
    {
        int gap=(int)Math.Ceiling(8*dpi/96.0),right=trayLeft-gap;
        if(required<=0||right-required<freeLeft+gap)return Rectangle.Empty;
        return new Rectangle(right-required,bar.Top,required,bar.Height);
    }

    internal bool Present(Rectangle bar,int freeLeft,int trayLeft,int dpi)
    {
        bool wasVisible=Visible;
        var a=Account();string text=a==null?(error??"额度不可用"):!a.supported?"额度不支持":"5h "+Percent(a.fiveHour)+" · 周 "+Percent(a.weekly);
        float scale=dpi/96f;
        using(var font=new Font(UiFonts.Taskbar,12*scale,FontStyle.Regular,GraphicsUnit.Pixel))
        using(var measure=new Bitmap(1,1))using(var graphics=Graphics.FromImage(measure))
        {
            int width=(int)Math.Ceiling(graphics.MeasureString(text,font).Width)+Math.Max(8,(int)(14*scale));
            Rectangle bounds=Place(bar,freeLeft,trayLeft,width,dpi);
            if(bounds.IsEmpty&&a!=null&&a.supported){text="5h"+Percent(a.fiveHour)+" 周"+Percent(a.weekly);width=(int)Math.Ceiling(graphics.MeasureString(text,font).Width)+(int)(14*scale);bounds=Place(bar,freeLeft,trayLeft,width,dpi);}
            if(bounds.IsEmpty){Hide();return false;}
            CurrentText=text;
            // Raise only our own independent window, without activation or ownership changes.
            // Explorer may raise its own topmost taskbar during minimize/restore.
            DesktopFacts.SetWindowPos(Handle,new IntPtr(-1),bounds.X,bounds.Y,bounds.Width,bounds.Height,0x10|0x200|0x40);
            if(!Visible)Show();
            if(bounds==lastBounds&&text==lastText)return true;
            using(var image=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppArgb))using(var drawing=Graphics.FromImage(image))
            {
                drawing.Clear(Color.FromArgb(1,0,0,0));drawing.TextRenderingHint=TextHint;
                bool light=object.Equals(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","SystemUsesLightTheme",0),1);
                using(var format=new StringFormat(StringFormat.GenericTypographic)) {
                    format.FormatFlags|=StringFormatFlags.MeasureTrailingSpaces;
                    if(a!=null&&a.supported){
                        bool compact=text.IndexOf(" · ",StringComparison.Ordinal)<0;
                        string[] parts={"5h"+(compact?"":" ")+Percent(a.fiveHour),compact?" ":" · ","周"+(compact?"":" ")+Percent(a.weekly)};
                        float[] sizes=parts.Select(p=>drawing.MeasureString(p,font,int.MaxValue,format).Width).ToArray();float x=(bounds.Width-sizes.Sum())/2;
                        bool current=AccountPresentation.ColorQuota(a);
                        Color[] colors={QuotaColor(current&&a.fiveHour!=null?a.fiveHour.remainingPercent:null,light),QuotaColor(null,light),QuotaColor(current&&a.weekly!=null?a.weekly.remainingPercent:null,light)};
                        float y=(bounds.Height-font.GetHeight(drawing))/2;
                        for(int i=0;i<parts.Length;i++){using(var brush=new SolidBrush(colors[i]))drawing.DrawString(parts[i],font,brush,new PointF(x,y),format);x+=sizes[i];}
                        if(a.stale||a.frozen||a.disabled||a.cacheStatus=="error")using(var dot=new SolidBrush(QuotaColor(40,light)))drawing.FillEllipse(dot,bounds.Width-4*scale,(bounds.Height-3*scale)/2,3*scale,3*scale);
                    }else using(var brush=new SolidBrush(QuotaColor(null,light))){format.Alignment=StringAlignment.Center;format.LineAlignment=StringAlignment.Center;drawing.DrawString(text,font,brush,new RectangleF(0,0,bounds.Width,bounds.Height),format);}
                }
                bool animate=AnimateChange(paintedText,text,paintedAlias,a==null?null:a.alias,wasVisible,a!=null&&AccountPresentation.ColorQuota(a),System.Windows.SystemParameters.ClientAreaAnimation);
                StopTransition();
                if(animate){transitionFrame=(Bitmap)image.Clone();transitionBounds=bounds;transitionClock.Restart();transition.Start();}
                PaintLayer(image,bounds,animate?TransitionAlpha(0):((byte)255));
            }
            paintedText=text;paintedAlias=a==null?null:a.alias;
            lastBounds=bounds;lastText=text;return true;
        }
    }
    internal static bool AnimateChange(string previousText,string nextText,string previousAlias,string nextAlias,bool visible,bool usable,bool motion)
    {
        return visible&&usable&&motion&&previousText!=null&&previousText!=nextText&&previousAlias!=null&&previousAlias==nextAlias;
    }
    internal static byte TransitionAlpha(double milliseconds)
    {
        double progress=Math.Max(0,Math.Min(1,milliseconds/200));return (byte)Math.Round(208+47*(1-Math.Pow(1-progress,3)));
    }
    private void StopTransition(){transition.Stop();transitionClock.Stop();if(transitionFrame!=null){transitionFrame.Dispose();transitionFrame=null;}}
    protected override void OnVisibleChanged(EventArgs e){if(!Visible){StopTransition();lastText=null;}base.OnVisibleChanged(e);}
    private void PaintLayer(Bitmap bitmap,Rectangle bounds,byte alpha)
    {
        IntPtr screen=GetDC(IntPtr.Zero),memory=CreateCompatibleDC(screen),image=bitmap.GetHbitmap(Color.FromArgb(0)),previous=SelectObject(memory,image);
        try {var dest=new Point(bounds.X,bounds.Y);var size=new LayerSize(bounds.Width,bounds.Height);var origin=new Point(0,0);var blend=new Blend {Alpha=alpha,Format=1};UpdateLayeredWindow(Handle,screen,ref dest,ref size,memory,ref origin,0,ref blend,2);}
        finally{SelectObject(memory,previous);DeleteObject(image);DeleteDC(memory);ReleaseDC(IntPtr.Zero,screen);}
    }
    protected override void Dispose(bool disposing){if(disposing){StopTransition();transition.Dispose();tooltip.Dispose();}base.Dispose(disposing);}
}
