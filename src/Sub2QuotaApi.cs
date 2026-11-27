using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class Sub2QuotaApi : IDisposable
{
    internal string Origin { get { return origin; } }
    private readonly HttpClient client;
    private readonly string origin;
    private readonly Func<string> loadRefresh;
    private readonly Action<string> saveRefresh;
    private readonly SemaphoreSlim session = new SemaphoreSlim(1, 1);
    private string access;
    private DateTimeOffset refreshAt;

    private static string CredentialTarget(string origin)
    {
        return "Sub2/QuotaTray/" + ServiceEndpoint.Normalize(origin);
    }

    internal Sub2QuotaApi(string origin) : this(
        new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }, origin,
        () => CredentialStore.Load(CredentialTarget(origin)),
        value => {
            string target = CredentialTarget(origin);
            if (value == null) CredentialStore.Remove(target);
            else CredentialStore.Save(target, value, "Sub2 member session");
        }) {}

    internal static Task<bool> DetectAsync(string origin, CancellationToken cancellation)
    {
        return DetectAsync(origin, new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }, cancellation);
    }

    internal static async Task<bool> DetectAsync(string origin, HttpMessageHandler handler, CancellationToken cancellation)
    {
        using (var probe = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) })
        using (var response = await probe.GetAsync(ServiceEndpoint.Normalize(origin) + "/api/v1/settings/public",
                                                  HttpCompletionOption.ResponseHeadersRead, cancellation))
        {
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.MethodNotAllowed)
                return false;
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType == null ||
                !String.Equals(response.Content.Headers.ContentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return false;
            using (var body = await response.Content.ReadAsStreamAsync())
            using (var buffer = new System.IO.MemoryStream()) {
                const int limit = 128 * 1024;
                var chunk = new byte[4096];
                int size;
                using (var read = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                    read.CancelAfter(TimeSpan.FromSeconds(6));
                    while ((size = await body.ReadAsync(chunk,0,chunk.Length,read.Token)) != 0) {
                        if (buffer.Length + size > limit) return false;
                        buffer.Write(chunk,0,size);
                    }
                }
                var serializer = new JavaScriptSerializer();
                Envelope<PublicSettings> reply;
                try { reply = serializer.Deserialize<Envelope<PublicSettings>>(Encoding.UTF8.GetString(buffer.ToArray())); }
                catch (ArgumentException) { return false; }
                catch (InvalidOperationException) { return false; }
                return reply != null && reply.code == 0 && reply.data != null &&
                    reply.data.registration_enabled.HasValue && reply.data.totp_enabled.HasValue &&
                    !String.IsNullOrEmpty(reply.data.site_name);
            }
        }
    }

    public sealed class PublicSettings
    {
        public bool? registration_enabled { get; set; }
        public bool? totp_enabled { get; set; }
        public string site_name { get; set; }
    }

    internal Sub2QuotaApi(HttpMessageHandler handler, string serverOrigin,
                          Func<string> loadRefresh, Action<string> saveRefresh)
    {
        origin = ServiceEndpoint.Normalize(serverOrigin);
        this.loadRefresh = loadRefresh;
        this.saveRefresh = saveRefresh;
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    internal bool HasSession() { return !String.IsNullOrEmpty(loadRefresh()); }

    internal sealed class SessionReply
    {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public int expires_in { get; set; }
        public bool requires_2fa { get; set; }
        public string temp_token { get; set; }
    }

    public sealed class BudgetReply
    {
        public Balance balance { get; set; }
    }

    public sealed class Balance
    {
        public long? limit { get; set; }
        public long? available { get; set; }
        public long? reserved { get; set; }
        public long? frozen { get; set; }
        public string starts_at { get; set; }
        public string ends_at { get; set; }
    }

    public sealed class Envelope<T>
    {
        public int code { get; set; }
        public T data { get; set; }
    }

    internal async Task<string> SignInAsync(string email, string password, CancellationToken cancellation)
    {
        await session.WaitAsync(cancellation);
        try {
            var reply = await Send<SessionReply>(HttpMethod.Post, "/auth/login",
                new { email = email, password = password }, null, false, cancellation);
            if (reply.requires_2fa) {
                if (String.IsNullOrWhiteSpace(reply.temp_token)) throw new InvalidOperationException("Missing Sub2 MFA challenge.");
                return reply.temp_token;
            }
            cancellation.ThrowIfCancellationRequested();
            Accept(reply);
            return null;
        } finally { session.Release(); }
    }

    internal async Task CompleteSignInAsync(string challenge, string code, CancellationToken cancellation)
    {
        await session.WaitAsync(cancellation);
        try {
            var reply = await Send<SessionReply>(HttpMethod.Post, "/auth/login/2fa",
                new { temp_token = challenge, totp_code = code }, null, false, cancellation);
            cancellation.ThrowIfCancellationRequested();
            Accept(reply);
        } finally { session.Release(); }
    }

    internal async Task ForgetAsync(CancellationToken cancellation)
    {
        await session.WaitAsync(cancellation);
        try {
            var refresh = loadRefresh();
            saveRefresh(null);
            access = null;
            if (!String.IsNullOrEmpty(refresh)) {
                try { await Send<object>(HttpMethod.Post, "/auth/logout",
                    new { refresh_token = refresh }, null, true, cancellation); }
                catch (HttpRequestException) {}
                catch (QuotaAuthException) {}
                catch (TaskCanceledException) {}
            }
        } finally { session.Release(); }
    }

    internal async Task<QuotaResult> ReadAsync(CancellationToken cancellation)
    {
        await session.WaitAsync(cancellation);
        try {
            try {
                if (access == null || DateTimeOffset.UtcNow >= refreshAt)
                    await RenewAsync(cancellation);
                return await ReadBudgetAsync(cancellation);
            } catch (QuotaAuthException e) {
                if (e.Unbound || access == null) throw;
                access = null;
            }
            await RenewAsync(cancellation);
            return await ReadBudgetAsync(cancellation);
        } finally { session.Release(); }
    }

    private async Task RenewAsync(CancellationToken cancellation)
    {
        var refresh = loadRefresh();
        if (String.IsNullOrEmpty(refresh)) throw new QuotaAuthException(false);
        access = null;
        Accept(await Send<SessionReply>(HttpMethod.Post, "/auth/refresh",
            new { refresh_token = refresh }, null, false, cancellation));
    }

    private void Accept(SessionReply reply)
    {
        if (String.IsNullOrWhiteSpace(reply.access_token) || String.IsNullOrWhiteSpace(reply.refresh_token) || reply.expires_in <= 0)
            throw new InvalidOperationException("Invalid Sub2 session.");
        saveRefresh(reply.refresh_token);
        access = reply.access_token;
        refreshAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, reply.expires_in - 30));
    }

    private async Task<QuotaResult> ReadBudgetAsync(CancellationToken cancellation)
    {
        var reply = await Send<BudgetReply>(HttpMethod.Get, "/team-budget", null, access, true, cancellation);
        return MakeQuota(reply == null ? null : reply.balance);
    }

    private async Task<T> Send<T>(HttpMethod method, string path, object body, string bearer,
                                  bool optional, CancellationToken cancellation) where T : class
    {
        using (var request = new HttpRequestMessage(method, origin + "/api/v1" + path))
        {
            var serializer = new JavaScriptSerializer();
            if (body != null) request.Content = new StringContent(serializer.Serialize(body), Encoding.UTF8, "application/json");
            if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using (var response = await client.SendAsync(request, cancellation))
            {
                if (optional && path == "/team-budget" && response.StatusCode == HttpStatusCode.NotFound) return null;
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    throw new QuotaAuthException(response.StatusCode == HttpStatusCode.Forbidden);
                response.EnsureSuccessStatusCode();
                if (optional && path == "/auth/logout") return null;
                var json = await response.Content.ReadAsStringAsync();
                var value = serializer.Deserialize<Envelope<T>>(json);
                if (value == null || value.code != 0 || value.data == null)
                    throw new InvalidOperationException("Invalid Sub2 response.");
                return value.data;
            }
        }
    }

    internal static QuotaResult MakeQuota(Balance balance)
    {
        string timestamp = DateTimeOffset.Now.ToString("o");
        var account = new QuotaAccount {
            alias = "sub2-personal-budget", displayName = "Sub2 个人额度", supported = true,
            availabilityKnown = true, available = true, lastSuccessAt = timestamp, personalQuota = true
        };
        if (balance == null) {
            account.quotaNote = "尚未分配额度，请联系管理员。";
        } else {
            DateTimeOffset start, end;
            if (!balance.limit.HasValue || !balance.available.HasValue || !balance.reserved.HasValue || !balance.frozen.HasValue ||
                balance.limit < 0 || balance.frozen < 0 || balance.reserved < 0 ||
                !DateTimeOffset.TryParse(balance.starts_at, CultureInfo.InvariantCulture, DateTimeStyles.None, out start) ||
                !DateTimeOffset.TryParse(balance.ends_at, CultureInfo.InvariantCulture, DateTimeStyles.None, out end) || start >= end)
                throw new InvalidOperationException("Invalid Sub2 quota.");
            account.quotaPeriod = (end - start) == TimeSpan.FromDays(7) ? "个人周额度" : "个人周期额度";
            if (balance.limit > 0) {
                account.weekly = new QuotaWindow {
                    remainingPercent = (double)Math.Floor(Math.Max(0m, Math.Min(100m,
                        100m * balance.available.Value / balance.limit.Value))),
                    resetAt = balance.ends_at
                };
            }
            var notes = new List<string>();
            if (balance.limit == 0) notes.Add("本周期未分配额度");
            if (balance.frozen > 0) notes.Add("待核对暂扣 " + balance.frozen.Value.ToString("N0"));
            if (balance.reserved > 0) notes.Add("在途预占 " + balance.reserved.Value.ToString("N0"));
            account.quotaNote = String.Join(" · ", notes.ToArray());
        }
        return new QuotaResult { accounts = new List<QuotaAccount> { account }, refreshStatus = "success" };
    }

    public void Dispose() { client.Dispose(); }
}
