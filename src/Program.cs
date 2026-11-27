using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Program
{
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private static bool TellInstance(uint message,IntPtr value){IntPtr window=DesktopFacts.FindWindow(null,"CPAplusQuota.UIHost.20261019");return window!=IntPtr.Zero&&PostMessage(window,message,value,IntPtr.Zero);}
    internal static readonly uint ShowMessage = RegisterWindowMessage("CPAplusQuota.UI.Show");
    internal static readonly uint ExitMessage = RegisterWindowMessage("CPAplusQuota.UI.Exit");
    internal static readonly uint ModeMessage = RegisterWindowMessage("CPAplusQuota.UI.Mode");

    [STAThread]
    private static void Main(string[] args)
    {
        SetCurrentProcessExplicitAppUserModelID("CPAplus.Quota.UI.20261019");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if(args.Contains("--enable-startup")||args.Contains("--disable-startup")){
            StartupRegistration.SetEnabled(args.Contains("--enable-startup"));return;
        }
        if(args.Contains("--show")){Environment.ExitCode=TellInstance(ShowMessage,IntPtr.Zero)?0:2;return;}
        if(args.Any(a=>a.StartsWith("--mode="))) { string mode=args.First(a=>a.StartsWith("--mode=")).Substring(7); int value=(int)(VisibilityMode)Enum.Parse(typeof(VisibilityMode),mode);Environment.ExitCode=TellInstance(ModeMessage,new IntPtr(value+1))?0:2;return; }
        if (args.Contains("--exit")) { Environment.ExitCode=TellInstance(ExitMessage,IntPtr.Zero)?0:2;return; }
        bool created;
        using (var singleton = new Mutex(true, "Local\\CPAplusQuota.UI.20261019", out created))
        {
            if (!created) { if(!args.Contains("--background"))TellInstance(ShowMessage,IntPtr.Zero); return; }
            // Diagnostic evidence is opt-in in the separate test runner.
            using (var context = new TrayContext(args.Contains("--preview"))) Application.Run(context);
        }
    }

    internal static Icon CreateIcon()
    {
        using (var stream = typeof(Program).Assembly.GetManifestResourceStream("QuotaIcon.ico"))
        using (var icon = new Icon(stream, SystemInformation.SmallIconSize))
        {
            return (Icon)icon.Clone();
        }
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly QuotaForm form;
    private readonly NotifyIcon tray;
    private readonly TrayMenu menu;
    private readonly Icon icon;
    private readonly TaskbarStrip strip;
    private readonly DesktopMonitor desktop;
    private QuotaApi api = new QuotaApi();
    private Sub2QuotaApi sub2;
    private Sub2QuotaApi draftSub2;
    private bool useSub2;
    private bool sub2Connected;
    private bool sub2Rejected;
    private bool sub2SigningOut;
    private bool HasConnection { get { return useSub2 ? sub2Connected : key != null; } }
    private string SourceLabel { get { return useSub2 ? "Sub2 额度" : "CPAplus 额度"; } }
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 15000 };
    private readonly ToolStripMenuItem pinMenu = new ToolStripMenuItem("置顶悬浮") { CheckOnClick = true };
    private CancellationTokenSource request;
    private bool readingCache;
    private int generation;
    private string key;
    private bool exiting;
    private bool active,activityInitialized;
    private long readAttempts;
    internal bool Active {get{return active;}}
    internal bool ReadTimerEnabled {get{return timer.Enabled;}}
    internal long ReadAttempts {get{return readAttempts;}}
    internal bool PopupInitialized {get{return form.PopupInitialized;}}

    internal TrayContext(bool show)
    {
        icon = Program.CreateIcon();
        form = new QuotaForm { Icon = icon };
        var handle = form.Handle;
        menu = new TrayMenu();
        strip = new TaskbarStrip();
        desktop = new DesktopMonitor(strip);
        strip.AccountSelected += delegate(string alias){form.SetPrimaryAlias(alias);};
        form.AccountSelected += delegate(string alias){strip.SelectAccount(alias);};
        form.PreferAccountRequested += async delegate(string alias){await SelectPreference(alias);};
        form.NotesChanged += delegate {strip.RefreshNames();};
        form.ExitRequested += delegate { ExitThread(); };
        strip.DetailsRequested += delegate { form.TogglePopup(); };
        var modes = new ToolStripMenuItem("显示时机");
        var labels = new[] { "Codex 图形应用运行时", "仅 Codex 在前台", "始终显示", "Codex 未最小化时显示", "隐藏任务栏额度" };
        Action<int> selectMode=delegate(int value){
            if(value<0||value>=labels.Length)return;
            try {desktop.Mode=(VisibilityMode)value;}
            catch(UnauthorizedAccessException){form.SetStatus("显示方式未能保存。",true);return;}
            catch(System.Security.SecurityException){form.SetStatus("Windows 不允许保存显示方式。",true);return;}
            form.SetDisplayMode(value);for(int i=0;i<modes.DropDownItems.Count;i++)((ToolStripMenuItem)modes.DropDownItems[i]).Checked=i==value;
            UpdateActivity();desktop.Reconcile();
        };
        for (int i=0;i<labels.Length;i++) {
            int choice=i; var item=new ToolStripMenuItem(labels[i]) { Checked=i==(int)desktop.Mode };
            item.Click += delegate {selectMode(choice);};
            modes.DropDownItems.Add(item);
        }
        form.ModeRequested += selectMode;
        var startupMenu=new ToolStripMenuItem("随 Windows 静默启动");
        startupMenu.Click+=delegate {SetStartup(!StartupRegistration.Enabled);};
        menu.Opening+=delegate {startupMenu.Checked=StartupRegistration.Enabled;};
        menu.Items.Add("查看额度", null, delegate { form.ShowPopup(); });
        pinMenu.CheckedChanged += delegate { form.SetPinned(pinMenu.Checked); };
        menu.Items.Add("刷新", null, async delegate { form.ShowPopup();await Refresh(true); });
        menu.Items.Add("服务地址…", null, delegate { Configure(); });
        var signOut = new ToolStripMenuItem("退出 Sub2 登录");
        menu.Opening += delegate { signOut.Visible = useSub2; };
        signOut.Click += async delegate { await SignOutSub2(); };
        menu.Items.Add(signOut);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(modes);
        menu.Items.Add(pinMenu);
        menu.Items.Add(startupMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { ExitThread(); });
        tray = new NotifyIcon { Icon = icon, Text = "CPAplus 额度", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) form.TogglePopup(); };
        form.RefreshRequested += async delegate { await Refresh(true); };
        form.ConfigureRequested += delegate { Configure(); };
        form.KeySaveRequested += SaveKey;
        form.ReuseSub2Session = HasSub2Session;
        form.AuthenticateSub2 = AuthenticateSub2;
        form.StartupRequested += SetStartup;
        form.PinRequested += delegate { pinMenu.Checked = !pinMenu.Checked; };
        timer.Tick += async delegate { await Refresh(); };
        try { key = string.IsNullOrEmpty(QuotaApi.BaseUrl)?null:CredentialStore.Load(CredentialStore.Target); }
        catch (System.ComponentModel.Win32Exception) { form.SetStatus("无法读取 Windows 凭据，请重新配置密钥。", true); }
        if (!QuotaApi.IsValidKey(key)) key = null;
        try {
            string origin=ServiceEndpoint.Sub2Server;
            useSub2=ServiceEndpoint.UseSub2&&origin!=null;
            if (useSub2) sub2=new Sub2QuotaApi(origin);
            LoadSub2Connection();
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
        form.PopupCreated+=delegate {
            form.SetSource(useSub2?sub2.Origin:null,sub2Connected,key);form.SetDisplayMode((int)desktop.Mode);form.SetPinned(pinMenu.Checked);form.SetBusy(request!=null&&!readingCache);form.SetStartup(StartupRegistration.Enabled);
            if(strip.CurrentData!=null)form.ShowAccounts(strip.CurrentData,strip.SelectedAlias);
            form.SetPaused(!active);
        };
        desktop.CodexRunningChanged+=delegate {UpdateActivity();};
        UpdateActivity();
        if (show) form.ShowPopup();
        if (!HasConnection) form.SetStatus(useSub2?"请连接 Sub2 成员账号。":"请在设置中配置服务地址并连接密钥。", false);
    }

    private void UpdateActivity(){SetActive(useSub2||desktop.CodexRunning||desktop.Mode==VisibilityMode.Always);}
    internal void SetActive(bool value)
    {
        if(activityInitialized&&active==value)return;
        activityInitialized=true;active=value;generation++;timer.Stop();
        timer.Interval = useSub2 ? 60000 : 15000;
        var previous=request;request=null;if(previous!=null)previous.Cancel();
        form.SetBusy(false);form.ClearAccounts();strip.UpdateData(null,value?"正在查询额度…":"等待 Codex");
        if(!value){strip.Hide();form.SuspendPopup();tray.Text="CPAplus 额度 · 等待 Codex";}
        else {form.SetPaused(false);if(HasConnection){timer.Start();var read=Refresh();}}
        WriteActivityEvidence();
    }
    private void SetStartup(bool enabled)
    {
        try {StartupRegistration.SetEnabled(enabled);form.SetStartup(StartupRegistration.Enabled);}
        catch(System.UnauthorizedAccessException){form.SetStartup(StartupRegistration.Enabled);form.SetStatus("无法修改开机启动项。",true);form.ShowPopup();}
        catch(System.Security.SecurityException){form.SetStatus("Windows 不允许修改启动项。",true);form.ShowPopup();}
    }
    private void WriteActivityEvidence()
    {
        if(DesktopMonitor.EvidenceDirectory==null)return;
        var json=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{time=DateTimeOffset.Now.ToString("o"),pid=System.Diagnostics.Process.GetCurrentProcess().Id,active=active,polling=timer.Enabled,readAttempts=readAttempts,popupCreated=form.PopupInitialized});
        System.IO.File.WriteAllText(System.IO.Path.Combine(DesktopMonitor.EvidenceDirectory,"resident-state.json"),json);
        System.IO.File.AppendAllText(System.IO.Path.Combine(DesktopMonitor.EvidenceDirectory,"resident-transitions.jsonl"),json+Environment.NewLine);
    }

    private void LoadSub2Connection()
    {
        try { sub2Connected = useSub2 && !sub2Rejected && sub2.HasSession(); }
        catch (System.ComponentModel.Win32Exception) {
            sub2Connected = false;
            form.SetStatus("Windows 无法读取 Sub2 登录。", true);
        }
    }

    private void ResetSource()
    {
        activityInitialized = false;
        form.KeySaved();
        form.SetSource(useSub2?sub2.Origin:null,sub2Connected,key);
        tray.Text = SourceLabel;
        UpdateActivity();
        if (!HasConnection) form.SetStatus(useSub2?"请连接 Sub2 成员账号。":"请连接 CPA 密钥。", false);
    }

    private void Configure()
    {
        if (!sub2SigningOut && !exiting) form.ShowKeyEditor(useSub2 ? null : key);
    }

    private Sub2QuotaApi GetDraftSub2(string origin)
    {
        if(sub2!=null&&sub2.Origin==origin)return sub2;
        if(draftSub2==null||draftSub2.Origin!=origin) {
            if(draftSub2!=null)draftSub2.Dispose();
            draftSub2=new Sub2QuotaApi(origin);
        }
        return draftSub2;
    }

    private bool HasSub2Session(string origin)
    {
        if(sub2SigningOut||(sub2Rejected&&sub2!=null&&sub2.Origin==origin))return false;
        try{return GetDraftSub2(origin).HasSession();}
        catch(System.ComponentModel.Win32Exception){return false;}
    }

    private async Task<string> AuthenticateSub2(string origin,string email,string secret,string challenge,CancellationToken cancellation)
    {
        if(exiting||sub2SigningOut)throw new InvalidOperationException("Sub2 connection unavailable.");
        var connection=GetDraftSub2(origin);
        if(challenge==null) {
            challenge=await connection.SignInAsync(email,secret,cancellation);
            if(challenge!=null)return challenge;
        } else await connection.CompleteSignInAsync(challenge,secret,cancellation);
        if(connection==sub2)sub2Rejected=false;
        return null;
    }

    private async Task SignOutSub2()
    {
        if (!useSub2 || sub2SigningOut || exiting) return;
        sub2SigningOut = true;
        sub2Connected = false;
        ResetSource();
        try { await sub2.ForgetAsync(CancellationToken.None); }
        catch (System.ComponentModel.Win32Exception) {
            LoadSub2Connection();
            ResetSource();
            form.SetStatus("退出失败，Windows 无法删除登录凭据。", true);
        }
        finally { sub2SigningOut = false; }
    }

    private void SaveKey(string next,bool memberLogin)
    {
        if (exiting || sub2SigningOut) return;
        string server;
        try { server = ServiceEndpoint.Normalize(form.ServerDraft); }
        catch (ArgumentException) { form.SetKeyError("请输入有效的 HTTPS 服务地址。"); return; }

        try {
            if (memberLogin) {
                var connection=GetDraftSub2(server);
                ServiceEndpoint.SaveSub2(server);
                ServiceEndpoint.UseSub2 = true;
                var previous=sub2;
                if(previous!=connection)sub2Rejected=false;
                sub2=connection;
                if(draftSub2==sub2)draftSub2=null;
                useSub2 = true;
                LoadSub2Connection();
                ResetSource();
                if(previous!=null&&previous!=sub2)previous.Dispose();
                return;
            }

            string target = "CPAplus/QuotaTray/" + server;
            if (string.IsNullOrEmpty(next)) next = CredentialStore.Load(target);
            if (!QuotaApi.IsValidKey(next)) {
                form.SetKeyError("首次连接此 CPA 地址时请输入有效的 API Key。");
                return;
            }
            if (server != QuotaApi.BaseUrl || next != key) CredentialStore.Save(target,next);
            if (server != QuotaApi.BaseUrl) {
                ServiceEndpoint.Save(server);
                AccountNames.Reload();
            }
            ServiceEndpoint.UseSub2 = false;
            useSub2 = false;
            var previousApi = api;
            api = new QuotaApi();
            key = next;
            ResetSource();
            previousApi.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { form.SetKeyError("Windows 无法读取或保存连接凭据。"); }
        catch (UnauthorizedAccessException) { form.SetKeyError("无法保存服务地址。"); }
        catch (System.Security.SecurityException) { form.SetKeyError("Windows 不允许保存服务地址。"); }
    }

    private async Task SelectPreference(string alias)
    {
        if(exiting||useSub2||!active||key==null||(request!=null&&!readingCache))return;
        int version=++generation;
        var previous=request;request=null;if(previous!=null)previous.Cancel();
        var cancellation=new CancellationTokenSource();request=cancellation;readingCache=false;form.SetBusy(true);
        try {
            form.SetReadFeedback("正在切换调用账号…");
            var data=await api.SelectAccountAsync(key,alias,cancellation.Token);
            if(exiting||generation!=version)return;
            if(data.selection==null)throw new InvalidOperationException("Missing selection response.");
            strip.UpdateData(data,null);
            if(alias!=null&&data.selection.mode=="manual"&&data.selection.alias==alias)strip.SelectAccount(alias);
            else if(data.selection.mode=="auto")strip.SelectDefaultAccount();
            form.ShowAccounts(data,strip.SelectedAlias);
            form.SetReadFeedback(data.selection.mode=="manual"?"已优先使用所选账号。":"已恢复按优先级自动排序。");
        }
        catch(QuotaAuthException){if(!exiting&&generation==version){timer.Stop();form.ClearAccounts();strip.UpdateData(null,"密钥不可用");form.SetStatus("密钥无效或绑定已变更，请重新连接。",true);}}
        catch(System.Net.Http.HttpRequestException){SelectionFailed(version);}
        catch(OperationCanceledException){SelectionFailed(version);}
        catch(InvalidOperationException){SelectionFailed(version);}
        catch(ArgumentException){SelectionFailed(version);}
        finally {if(request==cancellation){request=null;if(!exiting)form.SetBusy(false);}cancellation.Dispose();}
    }

    private void SelectionFailed(int version){if(!exiting&&generation==version)form.SetReadFeedback("切换未确认，请刷新账号状态后重试。");}

    internal async Task Refresh(bool manual=false)
    {
        if (exiting) return;
        if(request!=null){
            if(!manual||!readingCache)return;
            generation++;var previous=request;request=null;previous.Cancel();
        }
        if(!active||!HasConnection){if(manual)form.SetReadFeedback(!HasConnection?(useSub2?"请先连接 Sub2 成员账号。":"请先在设置中连接密钥。"):"Codex 未运行，额度查询已暂停。");return;}
        readAttempts++;
        int version = generation;
        var cancellation = new CancellationTokenSource();
        request = cancellation;
        readingCache=!manual;form.SetBusy(manual);
        try
        {
            if(manual&&!useSub2)form.SetReadFeedback("正在查询额度…");
            var alias=strip.SelectedAlias;
            var data = useSub2 ? await sub2.ReadAsync(cancellation.Token)
                : manual&&alias!=null?await api.RefreshAsync(key,alias,cancellation.Token):await api.ReadAsync(key,cancellation.Token);
            if (exiting || generation != version) return;
            strip.UpdateData(data,null);
            if(data.selection!=null&&data.selection.mode=="manual")strip.SelectAccount(data.selection.alias);
            else if(data.selection!=null)strip.SelectDefaultAccount();
            form.ShowAccounts(data,strip.SelectedAlias);
            if(manual&&!useSub2)form.SetReadFeedback(AccountPresentation.RefreshFeedback(data));
            tray.Text = useSub2 ? "Sub2 额度 · 个人预算" : "CPAplus 额度 · " + data.accounts.Count + " 个绑定账号";
        }
        catch (QuotaAuthException error)
        {
            if (exiting || generation != version) return;
            timer.Stop();
            form.ClearAccounts();
            if (useSub2) {
                sub2Rejected = true;
                sub2Connected = false;
                form.SetSource(sub2.Origin,false,key);
                strip.UpdateData(null,"请重新登录");
                form.SetStatus("Sub2 登录失效或无权查看，请重新登录成员账号。",true);
            } else {
                strip.UpdateData(null,"密钥不可用");
                form.SetStatus(error.Unbound ? "密钥未绑定账号。请切换密钥，或联系管理员。" : "密钥无效或已撤销。请切换密钥。", true);
            }
            tray.Text = SourceLabel+" · 连接需要处理";
        }
        catch (OperationCanceledException)
        {
            ReadFailed(version,true);
        }
        catch (System.ComponentModel.Win32Exception) { ReadFailed(version); }
        catch (System.Net.Http.HttpRequestException) { ReadFailed(version); }
        catch (InvalidOperationException) { ReadFailed(version); }
        catch (ArgumentException) { ReadFailed(version); }
        finally
        {
            if (request == cancellation) { request = null; if (!exiting) form.SetBusy(false); }
            cancellation.Dispose();
            if(!exiting)WriteActivityEvidence();
        }
    }

    private void ReadFailed(int version,bool timeout=false)
    {
        if (!exiting && generation == version) {
            string message=timeout?"查询超时":"暂时无法连接";
            bool retained=strip.CurrentData!=null;strip.UpdateData(strip.CurrentData,message);
            form.SetStatus(message+(retained?"，显示上次结果；将自动重试。":"，将自动重试。"),true);
            tray.Text=SourceLabel+" · "+message;
        }
    }

    protected override void ExitThreadCore()
    {
        exiting = true;
        timer.Stop(); timer.Dispose();
        desktop.Dispose(); strip.Close();strip.Dispose();
        if (request != null) request.Cancel();
        tray.Visible = false; tray.Dispose();menu.Dispose();
        api.Dispose(); if(sub2!=null)sub2.Dispose(); if(draftSub2!=null)draftSub2.Dispose(); form.AllowClose = true; form.Close(); icon.Dispose();
        base.ExitThreadCore();
    }
}
