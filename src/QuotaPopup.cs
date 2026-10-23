using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Web.Script.Serialization;

internal sealed class QuotaPopup : Window
{
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int left,right,top,bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition {public IntPtr window,after;public int x,y,width,height;public uint flags;}
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect {public int left,top,right,bottom;}
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out WindowRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd,int attribute,out int value,int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd,ref Margins value);
    private readonly System.Windows.Controls.Border root;
    private readonly bool light;
    private readonly PopupEdgeDock edgeDock;
    private readonly HashSet<string> expanded=new HashSet<string>();
    private QuotaResult data;
    private string selected,noteAlias;
    // WM_SIZE can update Height's base value mid-animation; retain the content target.
    private double desiredHeight=double.NaN;
    private bool resizing,resizeQueued,userPositioned;
    private int resizeBottom;
    private bool closing,editingKey,paused;
    private bool busy;
    internal event EventHandler RefreshRequested,ConfigureRequested,PinRequested,NotesChanged;
    internal event Action<int> ModeRequested;
    internal event Action<string> AccountSelected;
    internal event Action<string> PreferAccountRequested;
    internal event Action<string> KeySaveRequested;
    internal event Action<bool> StartupRequested;

    internal QuotaPopup()
    {
        Title="CPAplus 额度";Width=368;Height=400;SizeToContent=SizeToContent.Manual;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;
        WindowStartupLocation=WindowStartupLocation.Manual;Background=Brushes.Transparent;
        FontFamily=UiFonts.Popup;
        light=object.Equals(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","SystemUsesLightTheme",1),1);
        string[] keys={"Ink","Muted","Card","Stroke","Surface","Hover","Track","Accent","Warning"};
        string[] colors=light?new[]{"#242A31","#626B76","#88FFFFFF","#40FFFFFF","#B0FAFBFC","#1477808C","#1877808C","#236847","#8A5C10"}:new[]{"#EEF1F5","#ADB7C3","#552F3540","#28FFFFFF","#30202731","#20FFFFFF","#28FFFFFF","#8BCDAB","#D8BB7A"};
        for(int i=0;i<keys.Length;i++)Resources[keys[i]]=Brush(colors[i]);
        Resources["MenuSurface"]=Brush(light?"#F5F7FA":"#2D333C");
        Resources["RowSurface"]=Brush(light?"#B8FFFFFF":"#662F3540");
        Resources["RowStroke"]=Brush(light?"#1477808C":"#18FFFFFF");
        using(var stream=GetType().Assembly.GetManifestResourceStream("QuotaPopup.xaml"))root=(System.Windows.Controls.Border)XamlReader.Load(stream);
        root.VerticalAlignment=VerticalAlignment.Top;
        Content=new ScrollViewer {Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false};
        edgeDock=new PopupEdgeDock(this,delegate {return busy||editingKey||IsMouseCaptureWithin||B<StackPanel>("SettingsPage").Visibility==Visibility.Visible;},delegate {return B<CheckBox>("PinSetting").IsChecked==true;},Resource("Accent"));
        B<CheckBox>("EdgeSetting").IsChecked=edgeDock.Enabled;
        B<CheckBox>("EdgeSetting").Click+=delegate {edgeDock.SetEnabled(B<CheckBox>("EdgeSetting").IsChecked==true);};
        root.SizeChanged+=delegate(object sender,SizeChangedEventArgs e){
            if(!e.HeightChanged||resizeQueued)return;
            resizeQueued=true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(delegate {resizeQueued=false;if(!closing)ResizePopup(root.ActualHeight);}));
        };
        TextOptions.SetTextFormattingMode(root,TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(root,TextRenderingMode.Grayscale);
        UseLayoutRounding=true;
        B<Button>("CloseButton").Click+=delegate {Hide();};
        B<Button>("SettingsButton").Click+=delegate {Settings(true);};
        B<Button>("ConnectButton").Click+=delegate {Settings(true);};
        B<Button>("BackButton").Click+=delegate {NavigateBack();};
        B<Button>("RefreshButton").Click+=delegate {if(RefreshRequested!=null)RefreshRequested(this,EventArgs.Empty);};
        B<Button>("ChangeKeyButton").Click+=delegate {if(ConfigureRequested!=null)ConfigureRequested(this,EventArgs.Empty);};
        B<Button>("SaveNoteButton").Click+=delegate {SaveNote();};
        B<TextBox>("NoteInput").KeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Enter){SaveNote();e.Handled=true;}};
        B<Button>("UsePrimaryButton").Click+=delegate {if(MainAccount!=null&&PreferAccountRequested!=null)PreferAccountRequested(MainAccount.alias);};
        B<Button>("AutoRouteButton").Click+=delegate {if(PreferAccountRequested!=null)PreferAccountRequested(null);};
        B<Button>("CancelKeyButton").Click+=delegate {Settings(true);};
        B<Button>("SaveKeyButton").Click+=delegate {SaveKey();};
        B<PasswordBox>("KeyInput").PasswordChanged+=delegate {B<TextBlock>("KeyError").Visibility=Visibility.Collapsed;};
        B<PasswordBox>("KeyInput").KeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Enter){SaveKey();e.Handled=true;}};
        B<CheckBox>("PinSetting").Click+=delegate {if(PinRequested!=null)PinRequested(this,EventArgs.Empty);};
        B<CheckBox>("StartupSetting").Click+=delegate {if(StartupRequested!=null)StartupRequested(B<CheckBox>("StartupSetting").IsChecked==true);};
        B<ComboBox>("DisplayMode").ItemsSource=new[]{"Codex 运行时显示","仅 Codex 在前台时显示","始终显示","Codex 未最小化时显示","隐藏任务栏额度"};
        B<ComboBox>("DisplayMode").SelectedIndex=0;
        B<ComboBox>("DisplayMode").SelectionChanged+=delegate {if(ModeRequested!=null)ModeRequested(B<ComboBox>("DisplayMode").SelectedIndex);};
        B<TextBlock>("ServerAddress").Text=string.IsNullOrEmpty(QuotaApi.BaseUrl)?"尚未配置":QuotaApi.BaseUrl;
        PreviewMouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e){
            if(e.LeftButton==MouseButtonState.Pressed&&CanDragFrom(e.OriginalSource as DependencyObject)){e.Handled=true;DragMove();}
        };
        SourceInitialized+=delegate {ApplyMaterial();};
        SizeChanged+=delegate {if(IsVisible&&!Topmost&&!resizing)PositionPopup();};
        PreviewKeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Escape){var mode=B<ComboBox>("DisplayMode");if(mode.IsDropDownOpen)mode.IsDropDownOpen=false;else NavigateBack();e.Handled=true;}};
        IsVisibleChanged+=delegate {if(!IsVisible){resizing=false;if(!double.IsNaN(desiredHeight))Height=desiredHeight;BeginAnimation(HeightProperty,null);ClearKeyDraft();}};
        Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){if(!closing){e.Cancel=true;Hide();}};
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);
        Render();
    }
    private T B<T>(string name) where T:FrameworkElement {return (T)root.FindName(name);}
    private bool CanDragFrom(DependencyObject source)
    {
        for(var item=source;item!=null&&item!=this;item=item is Visual?VisualTreeHelper.GetParent(item):LogicalTreeHelper.GetParent(item)){
            if(item is System.Windows.Controls.Primitives.ButtonBase||item is System.Windows.Controls.Primitives.TextBoxBase||item is PasswordBox||item is ComboBox||item is Expander||item is System.Windows.Controls.Primitives.ScrollBar||item is System.Windows.Controls.Primitives.Thumb)return false;
        }
        return source!=null;
    }
    private static SolidColorBrush Brush(string value){return (SolidColorBrush)new BrushConverter().ConvertFromInvariantString(value);}
    private Brush Resource(string name){return (Brush)Resources[name];}
    private QuotaAccount MainAccount {get{return data==null?null:data.accounts.FirstOrDefault(a=>a.alias==selected)??data.accounts.FirstOrDefault();}}
    private Brush QuotaBrush(QuotaAccount a,QuotaWindow w)
    {
        if(!AccountPresentation.ColorQuota(a))return Resource("Muted");
        var c=TaskbarStrip.QuotaColor(w==null?null:w.remainingPercent,light);
        return new SolidColorBrush(Color.FromRgb(c.R,c.G,c.B));
    }
    private void ApplyMaterial()
    {
        var handle=new WindowInteropHelper(this).Handle;
        var source=HwndSource.FromHwnd(handle);source.CompositionTarget.BackgroundColor=Colors.Transparent;source.AddHook(KeepResizeAnchor);
        int dark=light?0:1,corner=2,backdrop=3;
        DwmSetWindowAttribute(handle,20,ref dark,4);DwmSetWindowAttribute(handle,33,ref corner,4);
        var margins=new Margins {left=-1,right=-1,top=-1,bottom=-1};DwmExtendFrameIntoClientArea(handle,ref margins);
        if(DwmSetWindowAttribute(handle,38,ref backdrop,4)!=0)root.Background=Brush(light?"#F4F7FA":"#252B33");
    }
    private IntPtr KeepResizeAnchor(IntPtr window,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        // Only an interactive move changes placement mode; programmatic layout does not.
        if(message==0x231){userPositioned=true;edgeDock.BeginMove();} // WM_ENTERSIZEMOVE
        if(message==0x232)edgeDock.EndMove(); // WM_EXITSIZEMOVE
        if(message==0x46&&edgeDock.Docked){
            var position=(WindowPosition)Marshal.PtrToStructure(lParam,typeof(WindowPosition));WindowRect current;
            if((position.flags&1)==0&&GetWindowRect(window,out current)){
                if((position.flags&2)!=0){position.x=current.left;position.y=current.top;}
                int top=edgeDock.ClampTop(position.y,position.height);
                if(top!=position.y){position.y=top;position.flags&=~2u;Marshal.StructureToPtr(position,lParam,false);}
            }
        }
        // Apply height and bottom anchoring in the same native operation, before painting.
        if(message==0x46&&resizing&&!Topmost&&!userPositioned){
            var position=(WindowPosition)Marshal.PtrToStructure(lParam,typeof(WindowPosition));
            WindowRect current;
            if((position.flags&1)==0&&GetWindowRect(window,out current)){
                position.x=current.left;position.y=resizeBottom-position.height;position.flags&=~2u;
                Marshal.StructureToPtr(position,lParam,false);
            }
        }
        return IntPtr.Zero;
    }
    internal void ShowPopup()
    {
        edgeDock.PrepareShow();
        if(!IsVisible){
            var handle=new WindowInteropHelper(this).EnsureHandle();
            var scale=HwndSource.FromHwnd(handle).CompositionTarget.TransformToDevice;
            VisualTreeHelper.SetRootDpi(this,new DpiScale(scale.M11,scale.M22));
            Settings(false);PositionPopup();
            root.Measure(new Size(Width,double.PositiveInfinity));
            root.Arrange(new Rect(0,0,Width,root.DesiredSize.Height));root.UpdateLayout();
            ResizePopup(root.DesiredSize.Height);
            PositionPopup();Show();
        }
        edgeDock.Shown();if(ShowActivated)Activate();WriteEvidence();
    }
    internal void TogglePopup(){if(IsVisible&&B<StackPanel>("HomePage").Visibility==Visibility.Visible)Hide();else ShowPopup();}
    private void NavigateBack()
    {
        if(editingKey)Settings(true);
        else if(B<StackPanel>("SettingsPage").Visibility==Visibility.Visible)Settings(false);
        else Hide();
    }
    private void PositionPopup()
    {
        var area=(userPositioned?System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle):System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position)).WorkingArea;
        var source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);var scale=source==null?Matrix.Identity:source.CompositionTarget.TransformFromDevice;
        MaxHeight=area.Height*scale.M22-24;
        B<ScrollViewer>("OtherScroll").MaxHeight=Math.Max(90,Math.Min(235,MaxHeight-370));
        double left=area.Left*scale.M11+12,top=area.Top*scale.M22+12;
        double right=Math.Max(left,area.Right*scale.M11-Width-12),bottom=Math.Max(top,area.Bottom*scale.M22-Math.Min(Height,MaxHeight)-12);
        Left=userPositioned?Math.Max(left,Math.Min(Left,right)):right;
        Top=userPositioned?Math.Max(top,Math.Min(Top,bottom)):bottom;
    }
    private void ResizePopup(double contentHeight)
    {
        double target=Math.Min(MaxHeight,contentHeight);
        if(Math.Abs(target-desiredHeight)<0.5)return;
        desiredHeight=target;double current=ActualHeight;
        if(IsVisible&&current>0){
            WindowRect bounds;GetWindowRect(new WindowInteropHelper(this).Handle,out bounds);resizeBottom=bounds.bottom;resizing=true;
            var animation=new DoubleAnimation(current,target,TimeSpan.FromMilliseconds(240)) {EasingFunction=new SineEase {EasingMode=EasingMode.EaseInOut}};
            animation.Completed+=delegate {Height=desiredHeight;BeginAnimation(HeightProperty,null);resizing=false;};
            BeginAnimation(HeightProperty,animation);
        }else {Height=target;BeginAnimation(HeightProperty,null);}
    }
    internal void SetData(QuotaResult next){data=next;B<TextBlock>("FailureText").Visibility=Visibility.Hidden;Render();}
    internal void SetAccounts(QuotaResult next,string alias){selected=alias;SetData(next);}
    internal void SetPrimary(string alias){if(selected==alias)return;selected=alias;Render();}
    internal void ClearData(){data=null;selected=null;noteAlias=null;expanded.Clear();B<Expander>("OtherAccounts").IsExpanded=false;Render();}
    internal void SetPinned(bool value){Topmost=value||edgeDock.Docked;B<CheckBox>("PinSetting").IsChecked=value;}
    internal void SetStartup(bool value){B<CheckBox>("StartupSetting").IsChecked=value;}
    internal void SetPaused(bool value){paused=value;Render();}
    internal void SetMode(int value){if(value>=0&&value<5)B<ComboBox>("DisplayMode").SelectedIndex=value;}
    internal void SetKeyLabel(string value){B<TextBlock>("KeyLabel").Text=string.IsNullOrEmpty(value)?"尚未配置密钥":"密钥已配置 · ··"+value.Substring(Math.Max(0,value.Length-2));B<TextBlock>("ServerAddress").Text=string.IsNullOrEmpty(QuotaApi.BaseUrl)?"尚未配置":QuotaApi.BaseUrl;}
    internal string ServerDraft {get{return B<TextBox>("ServerInput").Text;}}
    internal void ShowKeyEditor(string current)
    {
        ShowPopup();ClearKeyDraft();editingKey=true;
        B<TextBox>("ServerInput").Text=QuotaApi.BaseUrl;
        B<StackPanel>("HomePage").Visibility=Visibility.Collapsed;B<StackPanel>("SettingsPage").Visibility=Visibility.Collapsed;B<StackPanel>("KeyPage").Visibility=Visibility.Visible;
        B<TextBlock>("Heading").Text="更换密钥";B<Button>("BackButton").Visibility=Visibility.Visible;B<Button>("SettingsButton").Visibility=Visibility.Collapsed;
        B<TextBlock>("CurrentKeySummary").Text=string.IsNullOrEmpty(current)?"尚未配置密钥":"当前密钥 ··"+current.Substring(Math.Max(0,current.Length-2));
        Dispatcher.BeginInvoke(new Action(delegate {if(editingKey&&IsVisible)B<PasswordBox>("KeyInput").Focus();}));
    }
    internal void SetKeyError(string message){B<TextBlock>("KeyError").Text=message;B<TextBlock>("KeyError").Visibility=Visibility.Visible;}
    internal void KeySaved(){Settings(false);}
    private void ClearKeyDraft(){B<PasswordBox>("KeyInput").Clear();B<TextBlock>("KeyError").Visibility=Visibility.Collapsed;}
    private void SaveKey()
    {
        try {ServiceEndpoint.Normalize(ServerDraft);}
        catch(ArgumentException){SetKeyError("请输入 HTTPS 服务地址，不要包含路径、查询参数或登录信息。");return;}
        string value=B<PasswordBox>("KeyInput").Password.Trim();
        if(!QuotaApi.IsValidKey(value)){SetKeyError(value.Length==0?"请输入 CPA API Key。":"密钥不能包含空格或控制字符。");return;}
        if(KeySaveRequested!=null)KeySaveRequested(value);
    }
    internal void SetReadFeedback(string text)
    {
        B<TextBlock>("FailureText").Text=text;
        B<TextBlock>("FailureText").Foreground=Resource("Muted");
        B<TextBlock>("FailureText").Visibility=string.IsNullOrEmpty(text)?Visibility.Hidden:Visibility.Visible;
        if(B<StackPanel>("SettingsPage").Visibility==Visibility.Visible)B<TextBlock>("SettingsNotice").Text=text;
    }
    internal void SetBusy(bool value){busy=value;B<Button>("RefreshButton").IsEnabled=!busy;B<Button>("RefreshButton").Content=busy?"查询中…":"刷新";RenderOthers();RenderRouting();}
    private void RenderRouting()
    {
        bool supported=data!=null&&data.selection!=null;
        B<WrapPanel>("RoutingActions").Visibility=supported?Visibility.Visible:Visibility.Collapsed;
        B<Button>("UsePrimaryButton").IsEnabled=!busy&&supported&&AccountPresentation.Usable(MainAccount);
        B<Button>("AutoRouteButton").IsEnabled=!busy&&supported&&data.selection.mode=="manual";
    }
    internal void SetStatus(string text,bool error)
    {
        if(error){B<TextBlock>("FailureText").Foreground=Resource("Warning");B<TextBlock>("FailureText").Text=text;B<TextBlock>("FailureText").Visibility=Visibility.Visible;if(B<StackPanel>("SettingsPage").Visibility==Visibility.Visible)B<TextBlock>("SettingsNotice").Text=text;}
        else B<TextBlock>("SettingsNotice").Text=text;
    }
    private void Render()
    {
        var a=MainAccount;bool empty=a==null;
        B<TextBlock>("AccountName").ToolTip=empty?null:AccountNames.Label(a);
        B<TextBlock>("AccountName").Text=empty?(paused?"等待 Codex":"还没有账号"):AccountNames.Label(a);
        B<TextBlock>("AccountState").Text=empty?(paused?"已暂停":"在设置中连接你的密钥"):AccountPresentation.State(a);
        B<TextBlock>("AccountState").Foreground=!empty&&AccountPresentation.Usable(a)?Resource("Accent"):Resource("Muted");
        B<System.Windows.Controls.Border>("StateBadge").Background=Brush(!empty&&AccountPresentation.Usable(a)?light?"#12345F49":"#184FA97E":light?"#1077808C":"#2077808C");
        B<Grid>("Metrics").Visibility=empty||!a.supported?Visibility.Collapsed:Visibility.Visible;
        B<TextBlock>("UnavailableHint").Visibility=!empty&&(!a.supported||!AccountPresentation.Usable(a))?Visibility.Visible:Visibility.Collapsed;
        B<TextBlock>("UnavailableHint").Text=!empty&&!a.supported?"此账号暂不支持额度查询":"上方为最近记录，账号当前不参与调用。";
        B<TextBlock>("UpdatedText").Text=empty?(paused?"Codex 启动后自动更新":"尚未连接"):AccountPresentation.Updated(a.lastSuccessAt);
        string cache=empty?"":AccountPresentation.CacheState(a);
        bool warning=cache.Length>0;
        if(!empty&&cache.Length==0&&data.selection!=null){
            var preferred=data.accounts.FirstOrDefault(account=>account.alias==data.selection.alias);
            cache=data.selection.mode=="manual"&&preferred!=null?"优先调用："+AccountNames.Label(preferred):"调用：按优先级自动排序";
        }
        B<TextBlock>("CacheNote").Text=cache;B<TextBlock>("CacheNote").Visibility=cache.Length==0?Visibility.Hidden:Visibility.Visible;
        B<TextBlock>("CacheNote").Foreground=Resource(warning?"Warning":"Muted");
        B<Button>("ConnectButton").Visibility=empty?Visibility.Visible:Visibility.Collapsed;
        B<Button>("SaveNoteButton").IsEnabled=data!=null&&data.accounts.Any(account=>account.alias==noteAlias);
        if(!empty){
            B<StackPanel>("FiveMetric").Visibility=a.fiveHour==null?Visibility.Collapsed:Visibility.Visible;
            B<Grid>("Metrics").RowDefinitions[1].Height=new GridLength(a.fiveHour==null?0:19);
            Metric("Five",a,a.fiveHour);Metric("Week",a,a.weekly);
        }
        RenderOthers();
        RenderRouting();
    }
    private void Metric(string prefix,QuotaAccount a,QuotaWindow w)
    {
        var color=QuotaBrush(a,w);B<TextBlock>(prefix+"Value").Text=AccountPresentation.Percent(w);B<TextBlock>(prefix+"Value").Foreground=color;
        B<ProgressBar>(prefix+"Bar").Foreground=color;B<ProgressBar>(prefix+"Bar").Value=w==null||!w.remainingPercent.HasValue?0:Math.Max(0,Math.Min(100,w.remainingPercent.Value));
        B<TextBlock>(prefix+"Reset").Text=AccountPresentation.Reset(w==null?null:w.resetAt);
    }
    private TextBlock Text(string value,double size,bool muted)
    {
        return new TextBlock {Text=value,FontSize=size,Foreground=Resource(muted?"Muted":"Ink"),TextTrimming=TextTrimming.CharacterEllipsis};
    }
    private void RenderOthers()
    {
        var main=MainAccount;var list=B<StackPanel>("AccountRows");
        var others=data==null?new List<QuotaAccount>():data.accounts.Where(a=>main==null||a.alias!=main.alias).ToList();
        B<Expander>("OtherAccounts").Header="还有 "+others.Count+" 个账号";
        B<Expander>("OtherAccounts").Visibility=others.Count==0?Visibility.Collapsed:Visibility.Visible;
        foreach(var removed in list.Children.OfType<Expander>().Where(row=>!others.Any(a=>a.alias==(string)row.Tag)).ToList())list.Children.Remove(removed);
        expanded.RemoveWhere(alias=>data==null||!data.accounts.Any(a=>a.alias==alias));
        int index=0;
        foreach(var a in others){
            var row=list.Children.OfType<Expander>().FirstOrDefault(item=>(string)item.Tag==a.alias);
            if(row==null){
                var heading=new Grid();heading.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});heading.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
                var label=Text("",12,false);label.Margin=new Thickness(2,0,10,0);
                var compact=Text("",11,true);compact.MaxWidth=124;compact.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(compact,1);heading.Children.Add(label);heading.Children.Add(compact);
                var body=new StackPanel {Margin=new Thickness(10,3,10,10)};
                body.Children.Add(new System.Windows.Controls.Border {Height=1,Background=Resource("RowStroke"),Margin=new Thickness(0,0,0,5)});
                var action=new Button {HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(-9,0,0,7)};
                string alias=a.alias;action.Click+=delegate {
                    if(busy||data==null)return;
                    var current=data.accounts.FirstOrDefault(account=>account.alias==alias);if(current==null)return;
                    if(data.selection!=null&&AccountPresentation.Usable(current)){if(PreferAccountRequested!=null){action.Content="切换中…";PreferAccountRequested(alias);}}
                    else if(AccountSelected!=null)AccountSelected(alias);
                };body.Children.Add(action);
                foreach(string name in new[]{"Quota","FiveReset","WeekReset","State","Updated"}){var line=Text("",name=="Quota"?13:11,name!="Quota");line.Name=name;body.Children.Add(line);}
                row=new Expander {Tag=alias,Header=heading,Content=body,FontSize=12,IsExpanded=expanded.Contains(alias),Margin=new Thickness(0,0,0,7),Padding=new Thickness(7,2,7,2),Background=Resource("RowSurface"),BorderBrush=Resource("RowStroke"),BorderThickness=new Thickness(1),HorizontalContentAlignment=HorizontalAlignment.Stretch};
                row.Expanded+=delegate {expanded.Add(alias);};row.Collapsed+=delegate {expanded.Remove(alias);};list.Children.Insert(index,row);
            }else if(list.Children.IndexOf(row)!=index){list.Children.Remove(row);list.Children.Insert(index,row);}
            index++;
            var header=(Grid)row.Header;var nameText=(TextBlock)header.Children[0];var summary=(TextBlock)header.Children[1];
            nameText.Text=AccountNames.Label(a);nameText.ToolTip=nameText.Text;summary.Text=AccountPresentation.Summary(a);summary.ToolTip=summary.Text;
            var details=(StackPanel)row.Content;var choose=details.Children.OfType<Button>().Single();choose.IsEnabled=!busy;
            if(!busy||!object.Equals(choose.Content,"切换中…"))choose.Content=data.selection!=null&&AccountPresentation.Usable(a)?"优先使用并主显示":"设为主显示";
            SetRowLine(details,"Quota",a.supported?string.Join(" · ",AccountPresentation.QuotaParts(a,false)):null).Foreground=Resource(AccountPresentation.Usable(a)?"Ink":"Muted");
            SetRowLine(details,"FiveReset",a.supported&&a.fiveHour!=null?"五小时 "+AccountPresentation.Reset(a.fiveHour.resetAt):null);
            SetRowLine(details,"WeekReset",a.supported&&a.weekly!=null?"周额度 "+AccountPresentation.Reset(a.weekly.resetAt):null);
            string status=AccountPresentation.State(a),cached=AccountPresentation.CacheState(a);
            SetRowLine(details,"State",status+(cached.Length>0?" · "+cached:""));SetRowLine(details,"Updated",AccountPresentation.Updated(a.lastSuccessAt));
        }
    }
    private TextBlock SetRowLine(StackPanel details,string name,string value)
    {
        var line=details.Children.OfType<TextBlock>().Single(item=>item.Name==name);line.Text=value;line.Visibility=value==null?Visibility.Collapsed:Visibility.Visible;return line;
    }
    private void Settings(bool show)
    {
        ClearKeyDraft();editingKey=false;B<StackPanel>("KeyPage").Visibility=Visibility.Collapsed;
        B<StackPanel>("HomePage").Visibility=show?Visibility.Collapsed:Visibility.Visible;B<StackPanel>("SettingsPage").Visibility=show?Visibility.Visible:Visibility.Collapsed;
        B<Button>("BackButton").Visibility=show?Visibility.Visible:Visibility.Collapsed;B<Button>("SettingsButton").Visibility=show?Visibility.Collapsed:Visibility.Visible;B<TextBlock>("Heading").Text=show?"设置":"Codex 额度";
        if(show){var a=MainAccount;noteAlias=a==null?null:a.alias;B<TextBox>("NoteInput").Text=a==null?"":AccountNames.Note(a.alias)??"";B<TextBlock>("RawAlias").Text=a==null?"尚无账号":AccountNames.Label(a);B<Button>("SaveNoteButton").IsEnabled=a!=null;B<TextBlock>("SettingsNotice").Text="备注仅保存在这台电脑。";}
    }
    private void SaveNote()
    {
        var a=data==null?null:data.accounts.FirstOrDefault(account=>account.alias==noteAlias);if(a==null)return;
        try {AccountNames.Save(a.alias,B<TextBox>("NoteInput").Text);B<TextBlock>("SettingsNotice").Text="备注已保存";Render();if(NotesChanged!=null)NotesChanged(this,EventArgs.Empty);}
        catch(IOException){B<TextBlock>("SettingsNotice").Text="备注未能保存，请稍后重试。";}
        catch(UnauthorizedAccessException){B<TextBlock>("SettingsNotice").Text="无法保存本地备注。";}
    }
    internal void WriteEvidence()
    {
        if(!IsVisible||DesktopMonitor.EvidenceDirectory==null)return;int backdrop;var handle=new WindowInteropHelper(this).Handle;int hr=DwmGetWindowAttribute(handle,38,out backdrop,4);
        var a=MainAccount;
        File.WriteAllText(Path.Combine(DesktopMonitor.EvidenceDirectory,"ui-state.json"),new JavaScriptSerializer().Serialize(new{title=Title,width=ActualWidth,height=ActualHeight,backdropHresult=hr,backdrop=backdrop,mainAccount=a==null?null:AccountNames.Label(a),otherAccountsExpanded=B<Expander>("OtherAccounts").IsExpanded,visible=IsVisible}));
    }
    internal void Shutdown(){if(closing)return;closing=true;edgeDock.Dispose();Close();}
}
