using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Reflection;

internal static class Sub2QuotaProbe
{
    private const string ExampleSub2Origin="https://sub2.example.invalid";
    private const string Tokens = @"{""access_token"":""synthetic-access"",""refresh_token"":""synthetic-refresh"",""expires_in"":3600}";
    private const string Budget = @"{""balance"":{""limit"":1000,""available"":754,""frozen"":100,""reserved"":25,""starts_at"":""2026-11-23T08:00:00+08:00"",""ends_at"":""2026-11-30T08:00:00+08:00""}}";
    private static int passed;

    private static void Check(bool value, string test)
    {
        if (!value) throw new Exception(test);
        Console.WriteLine("PASS " + test);
        passed++;
    }

    private sealed class FakeHttp : HttpMessageHandler
    {
        internal readonly Queue<Func<HttpRequestMessage,HttpResponseMessage>> Route =
            new Queue<Func<HttpRequestMessage,HttpResponseMessage>>();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Check(request.RequestUri.GetLeftPart(UriPartial.Authority) == ExampleSub2Origin, "requests stay on the Sub2 HTTPS origin");
            return Task.FromResult(Route.Dequeue()(request));
        }

        internal void Reply(string path, HttpMethod method, HttpStatusCode code, string payload, string bearer = null, string field = null)
        {
            Route.Enqueue(request => {
                Check(request.RequestUri.AbsolutePath == "/api/v1" + path && request.Method == method, "only " + method + " " + path);
                Check((request.Headers.Authorization == null ? null : request.Headers.Authorization.Parameter) == bearer,
                    "correct bearer scope for " + path);
                if (field != null) Check(request.Content.ReadAsStringAsync().Result.Contains(field), "synthetic credential field on " + path);
                return new HttpResponseMessage(code) { Content = new StringContent("{\"code\":0,\"data\":" + payload + "}",System.Text.Encoding.UTF8,"application/json") };
            });
        }
    }

    [STAThread]
    private static int Main()
    {
        try {
            var discovery = new FakeHttp();
            discovery.Reply("/settings/public", HttpMethod.Get, HttpStatusCode.OK,
                @"{""registration_enabled"":false,""totp_enabled"":true,""site_name"":""Example gateway""}");
            Check(Sub2QuotaApi.DetectAsync(ExampleSub2Origin,discovery,CancellationToken.None).GetAwaiter().GetResult(),
                "public configuration identifies Sub2 without credentials");
            var fallback = new FakeHttp();
            fallback.Reply("/settings/public", HttpMethod.Get, HttpStatusCode.NotFound,"null");
            Check(!Sub2QuotaApi.DetectAsync(ExampleSub2Origin,fallback,CancellationToken.None).GetAwaiter().GetResult(),
                "unknown public endpoint follows the CPA credential flow");
            var generic = new FakeHttp();
            generic.Reply("/settings/public", HttpMethod.Get, HttpStatusCode.OK,@"{""site_name"":""Example site""}");
            Check(!Sub2QuotaApi.DetectAsync(ExampleSub2Origin,generic,CancellationToken.None).GetAwaiter().GetResult(),
                "unrelated JSON does not select Sub2");
            var scenario = new FakeHttp();
            string saved = null;
            scenario.Reply("/auth/login", HttpMethod.Post, HttpStatusCode.OK, @"{""requires_2fa"":true,""temp_token"":""synthetic-challenge""}", field:"synthetic-password");
            scenario.Reply("/auth/login/2fa", HttpMethod.Post, HttpStatusCode.OK, Tokens, field:"synthetic-challenge");
            scenario.Reply("/team-budget", HttpMethod.Get, HttpStatusCode.OK, Budget, "synthetic-access");
            scenario.Reply("/team-budget", HttpMethod.Get, HttpStatusCode.Unauthorized, "null", "synthetic-access");
            scenario.Reply("/auth/refresh", HttpMethod.Post, HttpStatusCode.OK, Tokens.Replace("synthetic-access", "rotated-access").Replace("synthetic-refresh", "rotated-refresh"), field:"synthetic-refresh");
            scenario.Reply("/team-budget", HttpMethod.Get, HttpStatusCode.NotFound, "null", "rotated-access");
            scenario.Reply("/auth/logout", HttpMethod.Post, HttpStatusCode.OK, "{}", field:"rotated-refresh");
            using (var api = new Sub2QuotaApi(scenario, ExampleSub2Origin, () => saved, value => saved = value)) {
                var challenge = api.SignInAsync("test@sub2.invalid","synthetic-password",CancellationToken.None).GetAwaiter().GetResult();
                Check(challenge == "synthetic-challenge" && saved == null, "MFA required before retaining a session");
                api.CompleteSignInAsync(challenge,"123456",CancellationToken.None).GetAwaiter().GetResult();
                Check(saved == "synthetic-refresh" && api.HasSession(), "only refresh token retained after login");
                var result = api.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
                var account = result.accounts.Single();
                Check(account.weekly.remainingPercent == 75 && account.fiveHour == null && result.selection == null, "weekly percentage floored and no fabricated five-hour window or routing");
                Check(account.quotaNote.Contains("待核对暂扣 100") && account.quotaNote.Contains("在途预占 25"), "pending and frozen displayed separately");
                var popup = new QuotaPopup();
                try {
                    popup.SetSource(ExampleSub2Origin,true,null);
                    popup.SetData(result);
                    var root = (FrameworkElement)typeof(QuotaPopup).GetField("root",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(popup);
                    Check(((TextBlock)root.FindName("WeekTitle")).Text=="个人周额度" &&
                          ((TextBlock)root.FindName("WeekValue")).Text=="75%", "WPF card shows personal weekly quota");
                    Check(((StackPanel)root.FindName("FiveMetric")).Visibility==Visibility.Collapsed &&
                          ((WrapPanel)root.FindName("RoutingActions")).Visibility==Visibility.Collapsed, "Sub2 card hides five-hour and routing controls");
                    Check(((TextBlock)root.FindName("CacheNote")).Text.Contains("待核对暂扣"), "WPF card shows frozen detail");
                    popup.SetData(new QuotaResult { accounts = new List<QuotaAccount>{
                        new QuotaAccount { alias="cpa-test", weekly=new QuotaWindow(), supported=true } },
                        selection=new AccountSelection { mode="auto" } });
                    popup.SetSource(null,true,"fake-bound");
                    Check(((WrapPanel)root.FindName("RoutingActions")).Visibility==Visibility.Visible &&
                          ((TextBlock)root.FindName("WeekTitle")).Text=="周额度", "switching to CPA restores its label and routing controls");
                    var button = (Button)root.FindName("SaveKeyButton");
                    var server = (TextBox)root.FindName("ServerInput");
                    var addressPanel = (StackPanel)root.FindName("AddressStep");
                    var cpaPanel = (StackPanel)root.FindName("CpaStep");
                    var sub2Panel = (StackPanel)root.FindName("Sub2Step");
                    var next = new Action(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                    var back = new Action(() => ((Button)root.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                    int committed = 0;
                    popup.KeySaveRequested += (value, memberLogin) => { committed++; popup.KeySaved(); };
                    popup.ReuseSub2Session = origin => false;
                    popup.DetectSub2 = (origin, token) => Task.FromResult(origin==ExampleSub2Origin);
                    popup.AuthenticateSub2 = (origin, mail, secret, step, token) => {
                        Check(origin==ExampleSub2Origin,"member credentials remain on the selected Sub2 origin");
                        if (step == null) {
                            Check(mail=="member@sub2.invalid" && secret=="synthetic-password", "Sub2 fields submit inside the existing WPF window");
                            return Task.FromResult("fixture-mfa");
                        }
                        Check(step=="fixture-mfa" && secret=="123456", "verification submits the six-digit code with the challenge");
                        return Task.FromResult<string>(null);
                    };
                    popup.ShowKeyEditor(null);
                    Check(addressPanel.Visibility==Visibility.Visible && cpaPanel.Visibility==Visibility.Collapsed &&
                        sub2Panel.Visibility==Visibility.Collapsed && (string)button.Content=="下一步", "first step only displays the service address");
                    server.Text="http://sub2.example.invalid";
                    next();
                    Check(addressPanel.Visibility==Visibility.Visible && committed==0, "invalid address stays in step one");
                    server.Text="https://cpa.example.invalid";
                    next();
                    Check(cpaPanel.Visibility==Visibility.Visible && sub2Panel.Visibility==Visibility.Collapsed &&
                        addressPanel.Visibility==Visibility.Collapsed, "CPA address reveals only the API Key step");
                    ((PasswordBox)root.FindName("KeyInput")).Password="fake-draft";
                    back();
                    Check(addressPanel.Visibility==Visibility.Visible &&
                        ((PasswordBox)root.FindName("KeyInput")).Password.Length==0, "back clears the draft and restores address entry");
                    server.Text=ExampleSub2Origin;
                    next();
                    Check(sub2Panel.Visibility==Visibility.Visible && cpaPanel.Visibility==Visibility.Collapsed &&
                        ((StackPanel)root.FindName("Sub2EmailRow")).Visibility==Visibility.Visible, "Sub2 address reveals only the member login step");
                    ((TextBox)root.FindName("Sub2Email")).Text="member@sub2.invalid";
                    ((PasswordBox)root.FindName("Sub2Secret")).Password="synthetic-password";
                    next();
                    Check((string)button.Content=="验证" && ((StackPanel)root.FindName("Sub2EmailRow")).Visibility==Visibility.Collapsed,
                        "MFA is shown only when the member login requires it");
                    ((PasswordBox)root.FindName("Sub2Secret")).Password="123456";
                    next();
                    Check(committed==1 && ((StackPanel)root.FindName("HomePage")).Visibility==Visibility.Visible,
                        "successful login closes the flow within the same window");
                    popup.ReuseSub2Session=origin=>origin==ExampleSub2Origin;
                    popup.ShowKeyEditor(null);
                    server.Text=ExampleSub2Origin;
                    next();
                    Check(committed==2 && ((StackPanel)root.FindName("HomePage")).Visibility==Visibility.Visible,
                        "existing Sub2 session is reused without extra credential fields");
                } finally { popup.Shutdown(); }
                var unavailable = api.ReadAsync(CancellationToken.None).GetAwaiter().GetResult().accounts.Single();
                Check(saved == "rotated-refresh" && unavailable.weekly == null && unavailable.quotaNote.Contains("尚未分配"), "refresh rotation and unconfigured budget handled");
                api.ForgetAsync(CancellationToken.None).GetAwaiter().GetResult();
                Check(saved == null && scenario.Route.Count == 0, "sign-out removes token and sends no extra requests");
            }
            var negative = Sub2QuotaApi.MakeQuota(new Sub2QuotaApi.Balance {
                limit=100, available=-5, reserved=0, frozen=0,
                starts_at="2026-11-27T08:00:00+08:00", ends_at="2026-11-28T08:00:00+08:00"
            }).accounts.Single();
            Check(negative.weekly.remainingPercent==0 && negative.quotaPeriod=="个人周期额度", "short custom period and negative availability handled");
            Console.WriteLine("Total " + passed + " passed.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
