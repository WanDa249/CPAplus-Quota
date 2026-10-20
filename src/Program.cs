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
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 15000 };
    private readonly ToolStripMenuItem pinMenu = new ToolStripMenuItem("置顶悬浮") { CheckOnClick = true };
    private CancellationTokenSource request;
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
        form.NotesChanged += delegate {strip.RefreshNames();};
        form.ExitRequested += delegate { ExitThread(); };
        strip.DetailsRequested += delegate { form.ShowPopup(); };
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
        menu.Items.Add("更换密钥…", null, delegate { Configure(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(modes);
        menu.Items.Add(pinMenu);
        menu.Items.Add(startupMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { ExitThread(); });
        tray = new NotifyIcon { Icon = icon, Text = "CPAplus 额度", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) form.ShowPopup(); };
        form.RefreshRequested += async delegate { await Refresh(true); };
        form.ConfigureRequested += delegate { Configure(); };
        form.KeySaveRequested += SaveKey;
        form.StartupRequested += SetStartup;
        form.PinRequested += delegate { pinMenu.Checked = !pinMenu.Checked; };
        timer.Tick += async delegate { await Refresh(); };
        try { key = string.IsNullOrEmpty(QuotaApi.BaseUrl)?null:CredentialStore.Load(CredentialStore.Target); }
        catch (System.ComponentModel.Win32Exception) { form.SetStatus("无法读取 Windows 凭据，请重新配置密钥。", true); }
        if (!QuotaApi.IsValidKey(key)) key = null;
        form.PopupCreated+=delegate {
            form.SetKeyLabel(key);form.SetDisplayMode((int)desktop.Mode);form.SetPinned(pinMenu.Checked);form.SetBusy(request!=null);form.SetStartup(StartupRegistration.Enabled);
            if(strip.CurrentData!=null)form.ShowAccounts(strip.CurrentData,strip.SelectedAlias);
            form.SetPaused(!active);
        };
        desktop.CodexRunningChanged+=delegate {UpdateActivity();};
        UpdateActivity();
        if (show) form.ShowPopup();
        if (key == null) form.SetStatus("请在设置中配置服务地址并连接密钥。", false);
    }

    private void UpdateActivity(){SetActive(desktop.CodexRunning||desktop.Mode==VisibilityMode.Always);}
    internal void SetActive(bool value)
    {
        if(activityInitialized&&active==value)return;
        activityInitialized=true;active=value;generation++;timer.Stop();
        var previous=request;request=null;if(previous!=null)previous.Cancel();
        form.SetBusy(false);form.ClearAccounts();strip.UpdateData(null,value?"正在读取缓存":"等待 Codex");
        if(!value){strip.Hide();form.SuspendPopup();tray.Text="CPAplus 额度 · 等待 Codex";}
        else {form.SetPaused(false);if(key!=null){timer.Start();var read=Refresh();}}
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

    private void Configure()
    {
        form.ShowKeyEditor(key);
    }

    private void SaveKey(string next)
    {
        if(!QuotaApi.IsValidKey(next)){form.SetKeyError("请输入有效的 CPA API Key。");return;}
        string server;
        try {server=ServiceEndpoint.Normalize(form.ServerDraft);}
        catch(ArgumentException){form.SetKeyError("请输入有效的 HTTPS 服务地址。");return;}
        bool changed=server!=QuotaApi.BaseUrl;
        try {
            CredentialStore.Save("CPAplus/QuotaTray/"+server,next);
            if(changed)ServiceEndpoint.Save(server);
        }
        catch (System.ComponentModel.Win32Exception) { form.SetKeyError("Windows 凭据保存失败，未切换密钥。"); return; }
        catch (UnauthorizedAccessException) { form.SetKeyError("服务地址未能保存，未切换连接。"); return; }
        catch (System.Security.SecurityException) { form.SetKeyError("Windows 不允许保存服务地址，未切换连接。"); return; }
        generation++;
        var previous = request;
        request = null;
        if (previous != null) previous.Cancel();
        api.Dispose();api=new QuotaApi();
        if(changed)AccountNames.Reload();
        key = next;
        form.SetKeyLabel(key);
        form.ClearAccounts();
        strip.UpdateData(null,"正在读取缓存");
        form.SetStatus("已保存到 Windows 凭据管理器。", false);
        form.KeySaved();
        if(active)timer.Start();
        var read = Refresh();
    }

    internal async Task Refresh(bool manual=false)
    {
        if (exiting || request != null) return;
        if(!active||key==null){if(manual)form.SetReadFeedback(key==null?"请先在设置中连接密钥。":"Codex 未运行，额度读取已暂停。");return;}
        readAttempts++;
        int version = generation;
        var cancellation = new CancellationTokenSource();
        request = cancellation;
        form.SetBusy(true);
        try
        {
            if(manual)form.SetReadFeedback("正在请求刷新…");
            var alias=strip.SelectedAlias;
            var data = manual&&alias!=null?await api.RefreshAsync(key,alias,cancellation.Token):await api.ReadAsync(key, cancellation.Token);
            if (exiting || generation != version) return;
            strip.UpdateData(data,null);
            form.ShowAccounts(data,strip.SelectedAlias);
            if(manual)form.SetReadFeedback(AccountPresentation.RefreshFeedback(data));
            tray.Text = "CPAplus 额度 · " + data.accounts.Count + " 个绑定账号";
        }
        catch (QuotaAuthException error)
        {
            if (exiting || generation != version) return;
            timer.Stop();
            form.ClearAccounts();
            strip.UpdateData(null,"密钥不可用");
            form.SetStatus(error.Unbound ? "密钥未绑定账号。请切换密钥，或联系管理员。" : "密钥无效或已撤销。请切换密钥。", true);
            tray.Text = "CPAplus 额度 · 密钥需要处理";
        }
        catch (OperationCanceledException)
        {
            if (!exiting && generation == version) { form.ClearAccounts(); strip.UpdateData(null,"缓存读取超时");form.SetStatus("读取超时，15 秒后重试；不会查询供应商。", true); }
        }
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

    private void ReadFailed(int version)
    {
        if (!exiting && generation == version) { form.ClearAccounts();strip.UpdateData(null,"缓存暂不可读"); form.SetStatus("服务器暂不可用，15 秒后重试。", true); }
    }

    protected override void ExitThreadCore()
    {
        exiting = true;
        timer.Stop(); timer.Dispose();
        desktop.Dispose(); strip.Close();strip.Dispose();
        if (request != null) request.Cancel();
        tray.Visible = false; tray.Dispose();menu.Dispose();
        api.Dispose(); form.AllowClose = true; form.Close(); icon.Dispose();
        base.ExitThreadCore();
    }
}
