using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

internal static class AccountNames
{
    internal static string PathName=ServerPath();
    private static readonly Dictionary<string,string> notes=Load();
    private static string ServerPath()
    {
        if(string.IsNullOrEmpty(QuotaApi.BaseUrl))return null;
        var uri=new Uri(QuotaApi.BaseUrl);
        string host=uri.DnsSafeHost.Replace(':','_')+(uri.IsDefaultPort?"":"_"+uri.Port);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CPAplusQuota",host,"account-notes.json");
    }
    internal static void Reload()
    {
        PathName=ServerPath();var updated=Load();notes.Clear();foreach(var item in updated)notes.Add(item.Key,item.Value);
    }
    private static Dictionary<string,string> Load()
    {
        try {return File.Exists(PathName)?new JavaScriptSerializer().Deserialize<Dictionary<string,string>>(File.ReadAllText(PathName)):new Dictionary<string,string>();}
        catch(IOException){return new Dictionary<string,string>();}
        catch(UnauthorizedAccessException){return new Dictionary<string,string>();}
        catch(ArgumentException){return new Dictionary<string,string>();}
    }
    internal static string Note(string alias){string name;return alias!=null&&notes.TryGetValue(alias,out name)?name:null;}
    internal static string Label(QuotaAccount account)
    {
        if(account==null)return "尚无账号";
        return Note(account.alias)??(!string.IsNullOrWhiteSpace(account.displayName)?account.displayName:Label(account.alias));
    }
    internal static string Label(string alias)
    {
        string note=Note(alias);if(!string.IsNullOrWhiteSpace(note))return note;
        if(string.IsNullOrEmpty(alias))return "尚无账号";
        string shortId=alias.Replace("账号 ","");return "账号 · "+shortId.Substring(0,Math.Min(6,shortId.Length));
    }
    internal static void Save(string alias,string name)
    {
        if(string.IsNullOrEmpty(alias))return;
        name=(name??"").Trim();if(name.Length>40)throw new ArgumentException("备注最多40个字符");
        var updated=new Dictionary<string,string>(notes);
        if(name.Length==0)updated.Remove(alias);else updated[alias]=name;
        Directory.CreateDirectory(Path.GetDirectoryName(PathName));
        File.WriteAllText(PathName,new JavaScriptSerializer().Serialize(updated));
        notes.Clear();foreach(var item in updated)notes.Add(item.Key,item.Value);
    }
}

internal static class AccountPresentation
{
    internal static bool Usable(QuotaAccount a){return a!=null&&a.availabilityKnown&&a.available&&!a.frozen&&!a.disabled;}
    internal static bool ColorQuota(QuotaAccount a){return Usable(a)&&a.supported&&a.cacheStatus!="error";}
    internal static string RefreshFeedback(QuotaResult data)
    {
        switch(data.refreshStatus){
        case "success":return "额度已更新。";
        case "cached":return "";
        case "deferred":return "请稍后再试，当前显示最近一次额度。";
        case "error":return "额度查询失败，保留上次结果；未更新成功时间。";
        case "unsupported":return "此账号不支持额度查询。";
        case "not_supported":return "当前显示最近一次额度，暂未获取到新结果。";
        default:return "查询完成，暂无新的额度信息。";
        }
    }
    internal static string State(QuotaAccount a)
    {
        if(a.frozen)return "已冻结";
        if(a.disabled)return "已停用";
        if(!a.availabilityKnown)return "状态未知";
        return a.available?"可用":"不可用";
    }
    internal static string CacheState(QuotaAccount a)
    {
        if(!a.supported)return "不支持额度查询";
        if(a.cacheStatus=="error")return a.stale?"数据已过期 · 查询失败":"查询失败 · 显示上次结果";
        if(a.stale)return "额度信息已过期";
        if(a.cacheStatus=="unavailable")return "暂无额度数据";
        return "";
    }
    internal static string Percent(QuotaWindow w){return w==null||!w.remainingPercent.HasValue?"—":w.remainingPercent.Value.ToString("0")+"%";}
    internal static string Reset(string value)
    {
        DateTimeOffset time;if(!DateTimeOffset.TryParse(value,out time))return "重置时间未知";
        var local=time.LocalDateTime;return (local.Date==DateTime.Today?"今天 ":local.ToString("M月d日 "))+local.ToString("HH:mm")+" 重置";
    }
    internal static string Updated(string value)
    {
        DateTimeOffset time;if(!DateTimeOffset.TryParse(value,out time))return "尚未更新";
        return "额度更新于 "+time.LocalDateTime.ToString(time.LocalDateTime.Date==DateTime.Today?"HH:mm":"M月d日 HH:mm");
    }
    internal static string Summary(QuotaAccount a)
    {
        if(!Usable(a))return State(a);
        return !a.supported?"不支持查询":Percent(a.fiveHour)+" / "+Percent(a.weekly)+(a.stale?" · 数据已过期":a.cacheStatus=="error"?" · 查询失败":"");
    }
}
