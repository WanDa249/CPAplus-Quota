using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
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
    private int resizeBottom,resizeTop,resizeLift;
    private bool closing,editingKey,paused;
    private bool busy;
    internal event EventHandler RefreshRequested,ConfigureRequested,PinRequested,NotesChanged;
    internal event Action<int> ModeRequested;
    internal event Action<string> AccountSelected;
    internal event Action<string> PreferAccountRequested;
    internal event Action<string,bool> KeySaveRequested;
    internal Func<string,bool> ReuseSub2Session;
    internal Func<string,CancellationToken,Task<bool>> DetectSub2;
    internal Func<string,string,string,string,CancellationToken,Task<string>> AuthenticateSub2;
    private enum ConnectionStep { Address, Cpa, Sub2, Verification }
    private ConnectionStep connectionStep;
    private CancellationTokenSource connectionRequest;
    private string challenge;
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
        B<Button>("ConnectButton").Click+=delegate {
            if (ConfigureRequested!=null) ConfigureRequested(this,EventArgs.Empty);
        };
        B<Button>("BackButton").Click+=delegate {NavigateBack();};
        B<Button>("RefreshButton").Click+=delegate {if(RefreshRequested!=null)RefreshRequested(this,EventArgs.Empty);};
        B<Button>("ChangeKeyButton").Click+=delegate {if(ConfigureRequested!=null)ConfigureRequested(this,EventArgs.Empty);};
        B<Button>("SaveNoteButton").Click+=delegate {SaveNote();};
        B<TextBox>("NoteInput").KeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Enter){SaveNote();e.Handled=true;}};
        B<Button>("UsePrimaryButton").Click+=delegate {if(MainAccount!=null&&PreferAccountRequested!=null)PreferAccountRequested(MainAccount.alias);};
        B<Button>("AutoRouteButton").Click+=delegate {if(PreferAccountRequested!=null)PreferAccountRequested(null);};
        B<Button>("CancelKeyButton").Click+=delegate {Settings(true);};
        B<Button>("SaveKeyButton").Click+=async delegate {await SubmitConnectionAsync();};
        B<PasswordBox>("KeyInput").PasswordChanged+=delegate {B<TextBlock>("KeyError").Visibility=Visibility.Collapsed;};
        foreach (string name in new[]{"ServerInput","KeyInput","Sub2Email","Sub2Secret"}) {
            B<Control>(name).KeyDown+=async delegate(object sender,KeyEventArgs e) {
                if(e.Key!=Key.Enter)return;
                e.Handled=true;
                await SubmitConnectionAsync();
            };
        }
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
            if(item is System.Windows.Controls.Primitives.ButtonBase||item is System.Windows.Controls.Primitives.TextBoxBase||item is PasswordBox||item is ComboBox||item is ComboBoxItem||item is Expander||item is System.Windows.Controls.Primitives.ScrollBar||item is System.Windows.Controls.Primitives.Thumb)return false;
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
        if(message==0x231){userPositioned=true;resizeLift=0;edgeDock.BeginMove();} // WM_ENTERSIZEMOVE
        if(message==0x232)edgeDock.EndMove(); // WM_EXITSIZEMOVE
        if(message==0x46&&edgeDock.Docked){
            var position=(WindowPosition)Marshal.PtrToStructure(lParam,typeof(WindowPosition));WindowRect current;
            if((position.flags&1)==0&&GetWindowRect(window,out current)){
                if((position.flags&2)!=0){position.x=current.left;position.y=current.top;}
                int top=edgeDock.ClampTop(position.y,position.height);
                if(top!=position.y){position.y=top;position.flags&=~2u;Marshal.StructureToPtr(position,lParam,false);}
            }
        }
        // Keep every resize frame on-screen, including manually placed and pinned popups.
        if(message==0x46&&resizing){
            var position=(WindowPosition)Marshal.PtrToStructure(lParam,typeof(WindowPosition));
            WindowRect current;
            if((position.flags&1)==0&&GetWindowRect(window,out current)){
                bool bottomAnchored=!Topmost&&!userPositioned;
                position.x=current.left;position.y=bottomAnchored?resizeBottom-position.height:resizeTop;position.flags&=~2u;
                var work=System.Windows.Forms.Screen.FromHandle(window).WorkingArea;
                int margin=(int)Math.Ceiling(12/HwndSource.FromHwnd(window).CompositionTarget.TransformFromDevice.M22);
                int proposed=(position.flags&2)!=0?current.top:position.y;
                int top=Math.Max(work.Top+margin,Math.Min(proposed,work.Bottom-position.height-margin));
                resizeLift=bottomAnchored?0:resizeTop-top;
                if(top!=proposed){if((position.flags&2)!=0)position.x=current.left;position.y=top;position.flags&=~2u;}
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
        if(editingKey&&connectionStep!=ConnectionStep.Address) {
            ClearKeyDraft();
            ShowConnectionStep(ConnectionStep.Address);
        }
        else if(editingKey)Settings(true);
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
        double requestedTop=Top+resizeLift*scale.M22;
        Top=userPositioned?Math.Max(top,Math.Min(requestedTop,bottom)):bottom;
        resizeLift=userPositioned?(int)Math.Round((requestedTop-Top)/scale.M22):0;
    }
    private void ResizePopup(double contentHeight)
    {
        double target=Math.Min(MaxHeight,contentHeight);
        if(Math.Abs(target-desiredHeight)<0.5)return;
        desiredHeight=target;double current=ActualHeight;
        if(IsVisible&&current>0){
            WindowRect bounds;GetWindowRect(new WindowInteropHelper(this).Handle,out bounds);resizeBottom=bounds.bottom;resizeTop=bounds.top+resizeLift;resizing=true;
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
    private string sub2Server;
    private bool sub2Mode {get{return sub2Server!=null;}}
    internal void SetSource(string origin, bool signedIn, string key)
    {
        sub2Server=origin;
        if (sub2Mode) {
            B<TextBlock>("KeyLabel").Text = signedIn?"成员已登录":"成员尚未登录";
            B<TextBlock>("ServerAddress").Text = sub2Server;
        } else SetKeyLabel(key);
        B<StackPanel>("AccountNoteSettings").Visibility = sub2Mode?Visibility.Collapsed:Visibility.Visible;
        B<Button>("ChangeKeyButton").Content = "服务地址";
        B<Button>("ConnectButton").Content = sub2Mode?"登录 Sub2":"连接服务";
        B<TextBlock>("ConnectionDetails").Text = sub2Mode
            ?"使用 HTTPS 读取 Sub2 个人额度，每 60 秒更新。登录会话保存在 Windows 凭据管理器。"
            :"使用 HTTPS 连接 CPA 服务。\n密钥保存在 Windows 凭据管理器。\n每 15 秒自动查询额度，更新时间以上次成功结果为准。";
        Render();
    }
    internal string ServerDraft {get{return B<TextBox>("ServerInput").Text;}}
    internal void ShowKeyEditor(string current)
    {
        ShowPopup();ClearKeyDraft();editingKey=true;
        B<TextBox>("ServerInput").Text=sub2Mode?sub2Server:QuotaApi.BaseUrl;
        B<StackPanel>("HomePage").Visibility=Visibility.Collapsed;
        B<StackPanel>("SettingsPage").Visibility=Visibility.Collapsed;
        B<StackPanel>("KeyPage").Visibility=Visibility.Visible;
        B<Button>("BackButton").Visibility=Visibility.Visible;
        B<Button>("SettingsButton").Visibility=Visibility.Collapsed;
        ShowConnectionStep(ConnectionStep.Address);
    }

    private void ShowConnectionStep(ConnectionStep step)
    {
        connectionStep=step;
        bool address=step==ConnectionStep.Address, cpa=step==ConnectionStep.Cpa;
        bool verify=step==ConnectionStep.Verification;
        B<StackPanel>("AddressStep").Visibility=address?Visibility.Visible:Visibility.Collapsed;
        B<StackPanel>("CpaStep").Visibility=cpa?Visibility.Visible:Visibility.Collapsed;
        B<StackPanel>("Sub2Step").Visibility=address||cpa?Visibility.Collapsed:Visibility.Visible;
        B<StackPanel>("Sub2EmailRow").Visibility=verify?Visibility.Collapsed:Visibility.Visible;
        B<TextBlock>("Sub2SecretLabel").Text=verify?"验证器验证码":"密码";
        AutomationProperties.SetName(B<PasswordBox>("Sub2Secret"),verify?"验证器验证码":"Sub2 密码");
        B<TextBlock>("ConnectionOrigin").Visibility=address?Visibility.Collapsed:Visibility.Visible;
        B<TextBlock>("ConnectionOrigin").Text=address?"":ServerDraft;
        B<TextBlock>("Heading").Text=address?"服务地址":cpa?"连接 CPA":verify?"验证 Sub2 登录":"登录 Sub2";
        B<Button>("SaveKeyButton").Content=address?"下一步":cpa?"连接":verify?"验证":"登录";
        B<Button>("SaveKeyButton").IsEnabled=true;
        B<TextBlock>("KeyError").Visibility=Visibility.Collapsed;
        string field=address?"ServerInput":cpa?"KeyInput":verify?"Sub2Secret":"Sub2Email";
        Dispatcher.BeginInvoke(new Action(delegate {if(editingKey&&IsVisible&&connectionStep==step)B<Control>(field).Focus();}));
    }

    internal void SetKeyError(string message)
    {
        B<TextBlock>("KeyError").Text=message;
        B<TextBlock>("KeyError").Visibility=Visibility.Visible;
    }

    internal void KeySaved(){Settings(false);}

    private void ClearKeyDraft()
    {
        var previous=connectionRequest;
        connectionRequest=null;
        if(previous!=null)previous.Cancel();
        challenge=null;
        B<TextBox>("ServerInput").IsEnabled=true;
        B<PasswordBox>("KeyInput").Clear();
        B<TextBox>("Sub2Email").Clear();
        B<PasswordBox>("Sub2Secret").Clear();
        B<TextBlock>("KeyError").Visibility=Visibility.Collapsed;
    }

    private async Task SubmitConnectionAsync()
    {
        if(!editingKey||connectionRequest!=null)return;
        if(connectionStep==ConnectionStep.Address) {
            string origin;
            try {origin=ServiceEndpoint.Normalize(ServerDraft);}
            catch(ArgumentException){SetKeyError("请输入完整的 HTTPS 服务地址。");return;}
            B<TextBox>("ServerInput").Text=origin;
            await DetectConnectionAsync(origin);
            return;
        }
        if(connectionStep==ConnectionStep.Cpa) {
            if(KeySaveRequested!=null)KeySaveRequested(B<PasswordBox>("KeyInput").Password.Trim(),false);
            return;
        }
        string email=B<TextBox>("Sub2Email").Text.Trim(), secret=B<PasswordBox>("Sub2Secret").Password;
        if(challenge==null&&(email.Length==0||secret.Length==0)) {
            SetKeyError("请输入成员邮箱和密码。");return;
        }
        if(challenge!=null&&(secret.Length!=6||secret.Any(c=>c<'0'||c>'9'))) {
            SetKeyError("请输入验证器生成的 6 位验证码。");return;
        }
        if(AuthenticateSub2==null)return;
        var cancellation=new CancellationTokenSource();
        connectionRequest=cancellation;
        B<PasswordBox>("Sub2Secret").Clear();
        B<Button>("SaveKeyButton").IsEnabled=false;
        B<Button>("SaveKeyButton").Content=challenge==null?"正在登录…":"正在验证…";
        B<TextBlock>("KeyError").Visibility=Visibility.Collapsed;
        try {
            string next=await AuthenticateSub2(ServerDraft,email,secret,challenge,cancellation.Token);
            if(connectionRequest!=cancellation)return;
            challenge=next;
            if(challenge!=null)ShowConnectionStep(ConnectionStep.Verification);
            else if(KeySaveRequested!=null)KeySaveRequested("",true);
        }
        catch(OperationCanceledException){if(connectionRequest==cancellation)SetKeyError("连接超时，请重试。");}
        catch(System.ComponentModel.Win32Exception){if(connectionRequest==cancellation)SetKeyError("Windows 无法保存登录。");}
        catch(HttpRequestException){ShowSub2SignInError(cancellation);}
        catch(QuotaAuthException){ShowSub2SignInError(cancellation);}
        catch(InvalidOperationException){ShowSub2SignInError(cancellation);}
        catch(ArgumentException){ShowSub2SignInError(cancellation);}
        finally {
            if(connectionRequest==cancellation){
                connectionRequest=null;
                B<Button>("SaveKeyButton").IsEnabled=true;
                B<Button>("SaveKeyButton").Content=challenge==null?"登录":"验证";
            }
            cancellation.Dispose();
        }
    }

    private async Task DetectConnectionAsync(string origin)
    {
        var cancellation=new CancellationTokenSource();
        connectionRequest=cancellation;
        var next=B<Button>("SaveKeyButton");
        var address=B<TextBox>("ServerInput");
        next.IsEnabled=false;
        address.IsEnabled=false;
        next.Content="正在检查…";
        B<TextBlock>("KeyError").Visibility=Visibility.Collapsed;
        try {
            bool sub2=await (DetectSub2??Sub2QuotaApi.DetectAsync)(origin,cancellation.Token);
            if(connectionRequest!=cancellation)return;
            if(sub2) {
                if(ReuseSub2Session!=null&&ReuseSub2Session(origin)&&KeySaveRequested!=null)
                    KeySaveRequested("",true);
                else ShowConnectionStep(ConnectionStep.Sub2);
            } else {
                B<TextBlock>("CurrentKeySummary").Text="已在此地址保存过密钥时可留空。";
                ShowConnectionStep(ConnectionStep.Cpa);
            }
        } catch(HttpRequestException) {if(connectionRequest==cancellation)SetKeyError("无法识别服务，请检查地址或稍后重试。");}
        catch(System.IO.IOException) {if(connectionRequest==cancellation)SetKeyError("服务连接中断，请重试。");}
        catch(OperationCanceledException) {if(connectionRequest==cancellation)SetKeyError("服务检查超时，请重试。");}
        finally {
            if(connectionRequest==cancellation) {
                connectionRequest=null;
                if(connectionStep==ConnectionStep.Address)next.Content="下一步";
                address.IsEnabled=true;
                next.IsEnabled=true;
            }
            cancellation.Dispose();
        }
    }

    private void ShowSub2SignInError(CancellationTokenSource cancellation)
    {
        if(connectionRequest==cancellation)
            SetKeyError(challenge==null?"登录失败。请检查成员账号和密码。":"验证失败。请检查当前验证码，或返回后重新登录。");
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
        B<TextBlock>("AccountState").Text=empty?(paused?"已暂停":sub2Mode?"请登录 Sub2 成员账号":"在设置中连接你的密钥"):AccountPresentation.State(a);
        B<TextBlock>("AccountState").Foreground=!empty&&AccountPresentation.Usable(a)?Resource("Accent"):Resource("Muted");
        B<System.Windows.Controls.Border>("StateBadge").Background=Brush(!empty&&AccountPresentation.Usable(a)?light?"#12345F49":"#184FA97E":light?"#1077808C":"#2077808C");
        B<Grid>("Metrics").Visibility=empty||!a.supported||(a.personalQuota&&a.weekly==null)?Visibility.Collapsed:Visibility.Visible;
        B<TextBlock>("UnavailableHint").Visibility=!empty&&(!a.supported||!AccountPresentation.Usable(a))?Visibility.Visible:Visibility.Collapsed;
        B<TextBlock>("UnavailableHint").Text=!empty&&!a.supported?"此账号暂不支持额度查询":"上方为最近记录，账号当前不参与调用。";
        B<TextBlock>("UpdatedText").Text=empty?(paused?"Codex 启动后自动更新":"尚未连接"):AccountPresentation.Updated(a.lastSuccessAt);
        string cache=empty?"":AccountPresentation.CacheState(a);
        if(!empty&&a.personalQuota&&cache.Length==0)cache=a.quotaNote??"";
        bool warning=cache.Length>0;
        if(!empty&&cache.Length==0&&data.selection!=null){
            var preferred=data.accounts.FirstOrDefault(account=>account.alias==data.selection.alias);
            cache=data.selection.mode=="manual"&&preferred!=null?"优先调用："+AccountNames.Label(preferred):"调用：按优先级自动排序";
        }
        B<TextBlock>("CacheNote").Text=cache;B<TextBlock>("CacheNote").Visibility=cache.Length==0?Visibility.Hidden:Visibility.Visible;
        B<TextBlock>("CacheNote").Foreground=Resource(warning?"Warning":"Muted");
        B<Button>("ConnectButton").Visibility=empty?Visibility.Visible:Visibility.Collapsed;
        B<Button>("SaveNoteButton").IsEnabled=data!=null&&data.accounts.Any(account=>!account.personalQuota&&account.alias==noteAlias);
        if(!empty){
            B<StackPanel>("FiveMetric").Visibility=a.fiveHour==null?Visibility.Collapsed:Visibility.Visible;
            B<Grid>("Metrics").RowDefinitions[1].Height=new GridLength(a.fiveHour==null?0:19);
            B<TextBlock>("WeekTitle").Text=a.personalQuota?(a.quotaPeriod??"个人额度"):"周额度";
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
