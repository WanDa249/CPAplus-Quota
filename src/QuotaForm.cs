using System;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;

internal static class Theme
{
    internal static string Time(string value){DateTimeOffset parsed;return DateTimeOffset.TryParse(value,out parsed)?parsed.ToLocalTime().ToString("MM-dd HH:mm"):"未知";}
}

// The hidden WinForms host retains the existing tray message loop and commands.
internal sealed class QuotaForm : Form
{
    private QuotaPopup popup;
    private string status;
    private bool statusError;
    internal bool PopupInitialized {get{return popup!=null;}}
    internal string ServerDraft {get{return popup==null?"":popup.ServerDraft;}}
    internal bool AllowClose;
    internal event EventHandler RefreshRequested,ConfigureRequested,PinRequested,ExitRequested,NotesChanged;
    internal event Action<int> ModeRequested;
    internal event Action<string> AccountSelected;
    internal event Action<string> PreferAccountRequested;
    internal event Action<string,bool> KeySaveRequested;
    internal Func<string,bool> ReuseSub2Session;
    internal Func<string,string,string,string,CancellationToken,Task<string>> AuthenticateSub2;
    internal event EventHandler PopupCreated;
    internal event Action<bool> StartupRequested;
    internal QuotaForm()
    {
        Text="CPAplusQuota.UIHost.20261019";ShowInTaskbar=false;
    }
    private void EnsurePopup()
    {
        if(popup!=null)return;
        popup=new QuotaPopup();
        popup.ReuseSub2Session=ReuseSub2Session;
        popup.AuthenticateSub2=AuthenticateSub2;
        popup.RefreshRequested+=delegate {if(RefreshRequested!=null)RefreshRequested(this,EventArgs.Empty);};
        popup.ConfigureRequested+=delegate {if(ConfigureRequested!=null)ConfigureRequested(this,EventArgs.Empty);};
        popup.PinRequested+=delegate {if(PinRequested!=null)PinRequested(this,EventArgs.Empty);};
        popup.NotesChanged+=delegate {if(NotesChanged!=null)NotesChanged(this,EventArgs.Empty);};
        popup.ModeRequested+=delegate(int value){if(ModeRequested!=null)ModeRequested(value);};
        popup.AccountSelected+=delegate(string alias){if(AccountSelected!=null)AccountSelected(alias);};
        popup.PreferAccountRequested+=delegate(string alias){if(PreferAccountRequested!=null)PreferAccountRequested(alias);};
        popup.KeySaveRequested+=delegate(string value,bool sub2){if(KeySaveRequested!=null)KeySaveRequested(value,sub2);};
        popup.StartupRequested+=delegate(bool value){if(StartupRequested!=null)StartupRequested(value);};
        if(PopupCreated!=null)PopupCreated(this,EventArgs.Empty);
        if(status!=null)popup.SetStatus(status,statusError);
    }
    internal void ShowPopup(){EnsurePopup();popup.ShowPopup();}
    internal void TogglePopup(){EnsurePopup();popup.TogglePopup();}
    internal void SetPinned(bool value){if(popup!=null)popup.SetPinned(value);}
    internal void SetStatus(string text,bool error){status=text;statusError=error;if(popup!=null)popup.SetStatus(text,error);}
    internal void SetReadFeedback(string text){if(popup!=null)popup.SetReadFeedback(text);}
    internal void SetBusy(bool value){if(popup!=null)popup.SetBusy(value);}
    internal void ClearAccounts(){status=null;if(popup!=null)popup.ClearData();}
    internal void ShowAccounts(QuotaResult data,string primaryAlias){status=null;if(popup!=null)popup.SetAccounts(data,primaryAlias);}
    internal void SetPrimaryAlias(string alias){if(popup!=null)popup.SetPrimary(alias);}
    internal void SetDisplayMode(int value){if(popup!=null)popup.SetMode(value);}
    internal void SetSource(string sub2Server, bool signedIn, string key) { if(popup!=null)popup.SetSource(sub2Server,signedIn,key); }
    internal void SetKeyLabel(string key){if(popup!=null)popup.SetKeyLabel(key);}
    internal void SetPaused(bool value){if(popup!=null)popup.SetPaused(value);}
    internal void SetStartup(bool value){if(popup!=null)popup.SetStartup(value);}
    internal void ShowKeyEditor(string current){EnsurePopup();popup.ShowKeyEditor(current);}
    internal void SetKeyError(string message){if(popup!=null)popup.SetKeyError(message);}
    internal void KeySaved(){if(popup!=null)popup.KeySaved();}
    internal void SuspendPopup(){if(popup!=null){popup.Shutdown();popup=null;}}
    protected override void WndProc(ref Message message)
    {
        if((uint)message.Msg==Program.ExitMessage){if(ExitRequested!=null)ExitRequested(this,EventArgs.Empty);return;}
        if((uint)message.Msg==Program.ModeMessage){if(ModeRequested!=null)ModeRequested(message.WParam.ToInt32()-1);return;}
        if((uint)message.Msg==Program.ShowMessage){ShowPopup();return;}
        base.WndProc(ref message);
    }
    protected override void OnFormClosed(FormClosedEventArgs e){SuspendPopup();base.OnFormClosed(e);}
    protected override void Dispose(bool disposing){if(disposing)SuspendPopup();base.Dispose(disposing);}
}

