using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

// Retain native menu navigation and commands; only replace their presentation.
internal sealed class TrayMenu : ContextMenuStrip
{
    private readonly MenuRenderer appearance=new MenuRenderer();
    private Font menuFont;
    internal TrayMenu(){ShowImageMargin=false;ShowCheckMargin=true;DropShadowEnabled=true;}
    protected override void OnItemAdded(ToolStripItemEventArgs e)
    {
        base.OnItemAdded(e);
        var item=e.Item as ToolStripMenuItem;
        if(item!=null&&item.HasDropDownItems){
            item.DropDown.Opening+=delegate {Prepare(item.DropDown);};
            item.DropDown.Opened+=delegate {Round(item.DropDown);};
        }
    }
    protected override void OnOpening(CancelEventArgs e){Prepare(this);base.OnOpening(e);}
    protected override void OnOpened(EventArgs e){Round(this);base.OnOpened(e);}
    private void Prepare(ToolStripDropDown menu)
    {
        if(menuFont==null)menuFont=new Font(UiFonts.Menu,10,FontStyle.Regular,GraphicsUnit.Point);
        appearance.Light=object.Equals(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","SystemUsesLightTheme",1),1);
        float scale=menu.DeviceDpi/96f;
        menu.Font=menuFont;menu.Renderer=appearance;menu.BackColor=appearance.Surface;menu.ForeColor=appearance.Ink;
        menu.Padding=new Padding(Scale(5,scale),Scale(6,scale),Scale(5,scale),Scale(6,scale));
        var list=menu as ToolStripDropDownMenu;if(list!=null){list.ShowImageMargin=false;list.ShowCheckMargin=true;}
        foreach(ToolStripItem item in menu.Items){
            item.AutoSize=false;item.Size=new Size(Scale(230,scale),Scale(item is ToolStripSeparator?13:34,scale));
            item.Margin=Padding.Empty;
        }
        menu.PerformLayout();
    }
    private static int Scale(int value,float scale){return (int)Math.Round(value*scale);}
    private static void Round(ToolStripDropDown menu)
    {
        using(var path=MenuRenderer.Rounded(new RectangleF(0,0,menu.Width,menu.Height),8*menu.DeviceDpi/96f)){
            var old=menu.Region;menu.Region=new Region(path);if(old!=null)old.Dispose();
        }
    }
    protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing&&menuFont!=null){menuFont.Dispose();menuFont=null;}}

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        internal bool Light=true;
        internal Color Surface {get{return Light?Color.FromArgb(247,248,250):Color.FromArgb(39,44,52);}}
        internal Color Ink {get{return Light?Color.FromArgb(36,42,49):Color.FromArgb(238,241,245);}}
        private Color Muted {get{return Light?Color.FromArgb(98,107,118):Color.FromArgb(173,183,195);}}
        private Color Accent {get{return Light?Color.FromArgb(35,104,71):Color.FromArgb(139,205,171);}}
        private Color Stroke {get{return Light?Color.FromArgb(220,224,230):Color.FromArgb(67,73,82);}}
        internal MenuRenderer(){RoundedEdges=false;}
        internal static GraphicsPath Rounded(RectangleF r,float radius)
        {
            float d=radius*2;var path=new GraphicsPath();
            path.AddArc(r.Left,r.Top,d,d,180,90);path.AddArc(r.Right-d,r.Top,d,d,270,90);
            path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.Left,r.Bottom-d,d,d,90,90);path.CloseFigure();return path;
        }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e){e.Graphics.Clear(Surface);}
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e){}
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            float s=e.ToolStrip.DeviceDpi/96f;e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=Rounded(new RectangleF(.5f,.5f,e.ToolStrip.Width-1,e.ToolStrip.Height-1),8*s))using(var pen=new Pen(Stroke))e.Graphics.DrawPath(pen,path);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var item=e.Item as ToolStripMenuItem;
            if(!e.Item.Enabled||!(e.Item.Selected||(item!=null&&item.DropDown.Visible)))return;
            float s=e.ToolStrip.DeviceDpi/96f;e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=Rounded(new RectangleF(0,1,e.Item.Width,e.Item.Height-2),5*s))
            using(var brush=new SolidBrush(Light?Color.FromArgb(233,237,241):Color.FromArgb(57,64,74)))e.Graphics.FillPath(brush,path);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var state=e.Graphics.Save();e.Graphics.TextRenderingHint=TextRenderingHint.AntiAlias;
            using(var brush=new SolidBrush(e.Item.Enabled?Ink:Muted))using(var format=new StringFormat {LineAlignment=StringAlignment.Center,HotkeyPrefix=HotkeyPrefix.Show,Trimming=StringTrimming.EllipsisCharacter})
                e.Graphics.DrawString(e.Text,e.TextFont,brush,e.TextRectangle,format);
            e.Graphics.Restore(state);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            float s=e.Item.Owner.DeviceDpi/96f,x=e.ArrowRectangle.Left+e.ArrowRectangle.Width/2f,y=e.ArrowRectangle.Top+e.ArrowRectangle.Height/2f;
            float direction=e.Direction==ArrowDirection.Left?-1:1;e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var pen=new Pen(Muted,1.3f*s))e.Graphics.DrawLines(pen,new[]{new PointF(x-direction*2*s,y-3*s),new PointF(x+direction*1*s,y),new PointF(x-direction*2*s,y+3*s)});
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            float s=e.ToolStrip.DeviceDpi/96f,x=e.ImageRectangle.Left+e.ImageRectangle.Width/2f,y=e.Item.Height/2f;
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var pen=new Pen(Accent,1.7f*s){StartCap=LineCap.Round,EndCap=LineCap.Round})e.Graphics.DrawLines(pen,new[]{new PointF(x-4*s,y),new PointF(x-1*s,y+3*s),new PointF(x+5*s,y-4*s)});
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int pad=(int)(8*e.ToolStrip.DeviceDpi/96f);using(var pen=new Pen(Stroke))e.Graphics.DrawLine(pen,pad,e.Item.Height/2,e.Item.Width-pad,e.Item.Height/2);
        }
    }
}
