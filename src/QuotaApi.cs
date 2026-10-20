using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class QuotaWindow
{
    public double? remainingPercent { get; set; }
    public string resetAt { get; set; }
}

internal sealed class QuotaAccount
{
    public string alias { get; set; }
    public string displayName { get; set; }
    public bool supported { get; set; }
    public bool frozen { get; set; }
    public bool disabled { get; set; }
    public bool available { get; set; }
    public bool availabilityKnown { get; set; }
    public string cacheStatus { get; set; }
    public bool stale { get; set; }
    public string lastSuccessAt { get; set; }
    public string lastAttemptAt { get; set; }
    public QuotaWindow fiveHour { get; set; }
    public QuotaWindow weekly { get; set; }
}

internal sealed class QuotaResult
{
    public List<QuotaAccount> accounts { get; set; }
    public string serverTime { get; set; }
    public int staleAfterSeconds { get; set; }
    public string refreshStatus { get; set; }
}

internal sealed class QuotaAuthException : Exception
{
    internal readonly bool Unbound;
    internal QuotaAuthException(bool unbound) { Unbound = unbound; }
}

internal sealed class QuotaApi : IDisposable
{
    internal static string BaseUrl {get{return ServiceEndpoint.Current??"";}}
    private readonly HttpClient client;
    private readonly string origin = BaseUrl;

    internal QuotaApi()
    {
        // Keep credentials on the configured HTTPS origin; use normal Windows certificate validation.
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        client.Timeout = TimeSpan.FromSeconds(125);
    }

    internal static bool IsValidKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 512) return false;
        foreach (char c in key) if (char.IsWhiteSpace(c) || char.IsControl(c) || c > 126) return false;
        return true;
    }

    internal QuotaApi(HttpMessageHandler handler, string serverOrigin)
    {
        var uri = new Uri(serverOrigin, UriKind.Absolute);
        if(uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("HTTPS origin required.");
        origin = uri.GetLeftPart(UriPartial.Authority);
        client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(125);
    }

    internal Task<QuotaResult> ReadAsync(string key, CancellationToken cancellation)
    {
        return SendAsync(key, null, cancellation);
    }

    internal Task<QuotaResult> RefreshAsync(string key, string alias, CancellationToken cancellation)
    {
        if(string.IsNullOrWhiteSpace(alias))throw new ArgumentException("Account required.");
        return SendAsync(key, alias, cancellation);
    }

    private async Task<QuotaResult> SendAsync(string key, string alias, CancellationToken cancellation)
    {
        if (!IsValidKey(key)) throw new ArgumentException("Invalid CPA API Key.");
        using (var request = new HttpRequestMessage(alias==null?HttpMethod.Get:HttpMethod.Post, origin + "/v1/me/quotas"+(alias==null?"":"/refresh")))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if(alias!=null)request.Headers.Add("X-Quota-Account",alias.Replace("账号 ",""));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using(var operation=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
            operation.CancelAfter(TimeSpan.FromSeconds(alias==null?8:125));
            using (var response = await client.SendAsync(request, operation.Token))
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    throw new QuotaAuthException(response.StatusCode == HttpStatusCode.Forbidden);
                if(alias!=null&&(response.StatusCode==HttpStatusCode.NotFound||response.StatusCode==HttpStatusCode.MethodNotAllowed)) {
                    var cached=await ReadAsync(key,cancellation);
                    cached.refreshStatus="not_supported";
                    return cached;
                }
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync();
                cancellation.ThrowIfCancellationRequested();
                var data = new JavaScriptSerializer().Deserialize<QuotaResult>(json);
                if (data == null || data.accounts == null) throw new InvalidOperationException("Invalid quota response.");
                return data;
            }
            }
        }
    }

    public void Dispose() { client.Dispose(); }
}
