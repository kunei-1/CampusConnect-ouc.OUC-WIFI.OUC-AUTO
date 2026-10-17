using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CampusConnect;

internal sealed class PortalController
{
    public WebView2 View { get; private set; } = new() { Dock = DockStyle.Fill };
    public event Action<string>? Status;
    public event Action? Submitted;
    public event Action? AddressChanged;
    public event Action<bool>? ProcessFailed;
    public bool IsActive { get; private set; }
    public bool RequiresInteraction { get; private set; }
    public bool IsAuthenticating => authenticationDeadline != null || (!finished && !RequiresInteraction && fillingGeneration == generation);
    public string Address {
        get {
            try { return View.CoreWebView2?.Source is { } source && source != "about:blank" ? source : ""; }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return ""; }
        }
    }
    private CampusProfile? profile;
    private string password = "";
    private int generation;
    private bool finished;
    private int fillingGeneration = -1;
    private string? bootstrapId;
    private DateTimeOffset? authenticationDeadline;
    private int observingGeneration = -1;
    private bool authenticationConfirmed;
    private bool internetConfirmed;
    private Task<string>? passwordPending;
    private bool sourceShown;
    private int reentries;
    private Task? initialization;
    private readonly Func<Task<CoreWebView2Environment>> getEnvironment;

    public PortalController(Func<Task<CoreWebView2Environment>> getEnvironment) => this.getEnvironment = getEnvironment;

    // Returned to the school page when a redirect leaves the configured site while
    // credentials are about to be used: the login page is re-opened on the saved address.
    internal const string ReentryMarker = "campusconnect-reentry";
    private static string ReentryPage(string address) => "data:text/html;charset=utf-8," +
        "%3Ctitle%3Ereturning%3C/title%3E%3Cmeta%20http-equiv%3D%22refresh%22%20content%3D%220%3Burl%3D" +
        Uri.EscapeDataString(address) + "%22%3E";

    // The school page only talks to the saved site. Plain http cannot protect the
    // account from a captured page, so automatic login requires https.
    internal static bool IsSchoolPage(CampusProfile active, Uri page) => active.AllowsAutomaticLogin
        && page.Scheme == "https" && CampusProfile.SameSite(page.AbsoluteUri, active.PortalUrl);

    // A gateway adds session parameters (wlanuserip, wlanacname, ...) to the saved
    // address, so anything beyond scheme, host, port and path is not a new page.
    internal static bool SamePage(Uri page, Uri other)
        => $"{page.Scheme}://{page.Authority}{page.AbsolutePath}".Equals(
            $"{other.Scheme}://{other.Authority}{other.AbsolutePath}", StringComparison.OrdinalIgnoreCase);

    // Only the pages the controller navigates itself to: empty page and the return stub.
    private static bool IsAcceptedPage(string uri) => uri == "about:blank" || uri.StartsWith("data:text/html", StringComparison.Ordinal);

    private static bool IsHttp(Uri uri) => uri.Scheme is "http" or "https";

    private async Task InitializeAsync()
    {
        var view = View;
        // Reuse the already initialized browser runtime; the school's browser
        // still has its own settings and cannot access the application's bridge.
        var environment = await getEnvironment();
        if (view != View || view.IsDisposed) return;
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.IsInPrivateModeEnabled = true;
        await view.EnsureCoreWebView2Async(environment, options);
        if (view != View || view.IsDisposed) return;
        view.CoreWebView2.ProcessFailed += (_, e) => {
            if (view != View || view.IsDisposed) return;
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or
                CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                ProcessFailed?.Invoke(e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited);
        };
        View.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
        View.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
        View.CoreWebView2.Settings.IsWebMessageEnabled = false;
        View.CoreWebView2.Settings.AreHostObjectsAllowed = false;
        View.CoreWebView2.SourceChanged += (_, _) => {
            if (Uri.TryCreate(View.CoreWebView2?.Source, UriKind.Absolute, out var source) && IsHttp(source)) sourceShown = true;
            AddressChanged?.Invoke();
        };
        View.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; Status?.Invoke("认证页请求打开新窗口，请在系统浏览器中手动处理。"); };
        View.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        View.CoreWebView2.DownloadStarting += (_, e) => e.Cancel = true;
        View.CoreWebView2.NavigationStarting += async (_, e) => {
            generation++;
            if (e.Uri != "about:blank" && CampusProfile.Origin(e.Uri) is null) { e.Cancel = true; }
            else if (profile is not null && Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) &&
                     !IsSchoolPage(profile, target) && !IsAcceptedPage(e.Uri)) {
                // Credentials never leave the saved site. A refresh back to the saved page
                // (the gateway merely renews wlanuserip) looks like a redirect, so the
                // login page is re-opened instead of leaving a blank window behind.
                var refresh = target.Scheme == "https" && sourceShown &&
                    CampusProfile.SameSite(e.Uri, profile.PortalUrl);
                e.Cancel = true;
                sourceShown = false;
                Status?.Invoke(refresh && reentries < 2
                    ? "认证地址的会话参数已更新，正在重新打开学校认证页…"
                    : "页面已跳转到保存地址之外的网址，已阻止自动填写（http 地址也不会自动登录）；如属学校页面，请在设置中确认后更新认证地址。");
                if (refresh && reentries < 2) { reentries++; ReturnToLoginPage(); }
            }
            if (authenticationDeadline != null) { await Task.Delay(100); await ObserveAuthenticationAsync(); }
        };
        View.CoreWebView2.ContentLoading += async (_, _) => await TryFillAsync();
        View.CoreWebView2.DOMContentLoaded += async (_, _) => await TryFillAsync();
        View.CoreWebView2.NavigationCompleted += async (_, e) => {
            if (profile is null) return;
            if (!e.IsSuccess) { Status?.Invoke($"认证页加载失败：{e.WebErrorStatus}。可先断开 VPN 后重试。"); return; }
            if (authenticationDeadline != null) await ObserveAuthenticationAsync();
            else {
                if (await CheckAuthenticationPageAsync()) return;
                await FillWhenPasswordReadyAsync();
            }
        };
    }

    // The login page loads while the decrypted password is still on its way. The fill
    // waits for it, so nothing has to reload the school page afterwards.
    private async Task FillWhenPasswordReadyAsync()
    {
        var expected = generation;
        try { await FillAsync(expected); }
        catch (Exception ex) when (expected == generation) { Status?.Invoke("自动填写失败：" + ex.Message); }
    }

    public async Task OpenAsync(CampusProfile saved, string secret, bool discover = false, Task<string>? passwordWhenReady = null)
    {
        Stop(clearView: false);
        var expected = generation;
        initialization ??= InitializeAsync();
        try { await initialization; }
        catch { initialization = null; throw; }
        if (expected != generation) return;
        var connected = await CampusNetwork.IsAvailableAsync(saved);
        if (expected != generation || View.IsDisposed) return;
        if (!connected) throw new InvalidOperationException(saved.IsWired ? "请先插好网线并启用有线网络，再打开认证页。" : "请先连接已设置的校园 Wi-Fi，再打开认证页。");
        saved.Validate();
        profile = discover ? saved with { PortalUrl = "http://www.msftconnecttest.com/redirect", Username = "", AutoSubmit = false } : saved;
        password = discover ? "" : secret; RequiresInteraction = !saved.AutoSubmit;
        passwordPending = discover ? null : passwordWhenReady;
        sourceShown = false; reentries = 0;
        if (profile.PortalUrl.StartsWith(ReentryMarker, StringComparison.Ordinal) && profile.PortalUrl.Length > ReentryMarker.Length)
            profile = profile with { PortalUrl = profile.PortalUrl[ReentryMarker.Length..] };
        if (profile.PortalUrl.StartsWith("data:text/html", StringComparison.Ordinal))
            throw new InvalidOperationException("认证地址无效，请在设置中重新填写学校认证页地址。");
        if (bootstrapId != null) View.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(bootstrapId);
        using var stream = typeof(PortalController).Assembly.GetManifestResourceStream("CampusConnect.PortalBootstrap.js")!;
        using var reader = new StreamReader(stream);
        var bootstrap = reader.ReadToEnd().Replace("/*CAMPUS_ORIGIN*/", JsonSerializer.Serialize(CampusProfile.Origin(profile.PortalUrl)));
        bootstrapId = await View.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(bootstrap);
        if (expected != generation || profile is null) return;
        IsActive = true;
        View.CoreWebView2.Navigate(profile.PortalUrl);
        Status?.Invoke(discover ? "正在探测认证地址，请确认学校页面后使用当前地址。" : "正在打开校园认证页…");
    }
    // authenticated: the school page confirmed this account, not merely a slow probe.
    public void ConfirmInternetAccess(bool authenticated = true)
    {
        internetConfirmed = true; RequiresInteraction = false;
        authenticationDeadline = null; finished = true; password = "";
        Status?.Invoke(authenticated ? "已经连接成功" : "网络已恢复");
    }
    public void ReportInternetUnavailable() { internetConfirmed = false; Status?.Invoke("未连接网络"); }
    public void ResumeView()
    {
        try {
            View.CoreWebView2?.Resume();
            View.Invalidate(true);
            View.Update();
        } catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) {
            ProcessFailed?.Invoke(false);
        }
    }
    public void ResetBrowser()
    {
        generation++;
        profile = null; password = ""; finished = false; IsActive = false;
        authenticationDeadline = null; authenticationConfirmed = false; internetConfirmed = false; observingGeneration = -1;
        // Never re-submit a login/captcha just because the UI browser was restarted.
        RequiresInteraction = true;
        initialization = null; bootstrapId = null; fillingGeneration = -1; sourceShown = false; reentries = 0;
        var oldView = View;
        View = new() { Dock = DockStyle.Fill };
        oldView.Dispose();
    }
    // The browser stays on the blocked page, so the saved login page is loaded through a
    // short forward that does not look like a redirect away from the school.
    private void ReturnToLoginPage()
    {
        if (profile is not { } active) return;
        try { View.CoreWebView2.Navigate(ReentryPage(active.PortalUrl)); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }
    public void Stop(bool clearView = true)
    {
        generation++;
        profile = null; password = ""; finished = false; authenticationDeadline = null; authenticationConfirmed = false; internetConfirmed = false;
        IsActive = false; RequiresInteraction = false; sourceShown = false; reentries = 0;
        try {
            if (View.CoreWebView2 is not null) {
                View.CoreWebView2.Stop();
                if (clearView) View.CoreWebView2.Navigate("about:blank");
            }
        } catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }
    private async Task<bool> CheckAuthenticationPageAsync()
    {
        var expected = generation;
        if (profile is not { } active) return true;
        try {
            if (!IsSchoolPage(active, new Uri(View.CoreWebView2.Source))) return false;
            var connected = await CampusNetwork.IsAvailableAsync(active);
            if (expected != generation || View.IsDisposed) return true;
            if (!connected) { Stop(); return true; }
            var result = JsonSerializer.Deserialize<string>(await View.CoreWebView2.ExecuteScriptAsync(PortalScript.Build(active, "", statusOnly: true)));
            if (expected != generation) return true;
            if (result == "authenticated") { ConfirmAuthentication(); return true; }
        } catch { /* A navigation can destroy the old script context. */ }
        return expected != generation;
    }
    private void ConfirmAuthentication()
    {
        if (authenticationConfirmed) return;
        authenticationConfirmed = true; authenticationDeadline = null;
        // The page finished its job; leaving the browser parked in a "submitted" state
        // made a later visit look like a new login and replay it.
        finished = true; password = ""; RequiresInteraction = true; IsActive = false;
        if (!internetConfirmed) Status?.Invoke("学校已确认登录成功，正在检查实际联网状态。");
        Submitted?.Invoke();
    }
    private async Task ObserveAuthenticationAsync()
    {
        var expected = generation;
        if (observingGeneration == expected || authenticationDeadline == null) return;
        observingGeneration = expected;
        try {
            while (expected == generation && profile is { } active && authenticationDeadline is { } deadline) {
                var connected = await CampusNetwork.IsAvailableAsync(active);
                if (expected != generation || View.IsDisposed) return;
                if (!connected) { Stop(); Status?.Invoke("校园网已断开，停止登录校验。"); return; }
                string? result = null;
                if (Uri.TryCreate(View.CoreWebView2.Source, UriKind.Absolute, out var page) && IsSchoolPage(active, page))
                    result = JsonSerializer.Deserialize<string>(await View.CoreWebView2.ExecuteScriptAsync(PortalScript.Build(active, "", statusOnly: true)));
                if (expected != generation || authenticationDeadline == null) return;
                if (result == "authenticated") { ConfirmAuthentication(); return; }
                if (result == "authentication-rejected") {
                    authenticationDeadline = null; RequiresInteraction = true;
                    Status?.Invoke("学校返回登录失败，请查看网页提示并检查账号、密码和服务选项。"); return;
                }
                if (DateTimeOffset.UtcNow >= deadline) {
                    authenticationDeadline = null; RequiresInteraction = true;
                    Status?.Invoke("30 秒内未确认已登录页面或学校成功结果，正在复查联网状态；仍未联网时请查看网页提示后重试。");
                    Submitted?.Invoke(); return;
                }
                await Task.Delay(500);
            }
        } catch (Exception) {
            if (expected == generation) {
                authenticationDeadline = null; RequiresInteraction = true;
                Status?.Invoke("登录结果校验中断，请检查网页结果或重新认证。");
            }
        } finally { if (observingGeneration == expected) observingGeneration = -1; }
    }
    private async Task TryFillAsync()
    {
        var expected = generation;
        if (!IsActive || fillingGeneration == expected || finished || profile is null) return;
        fillingGeneration = expected;
        try { await FillAsync(expected); }
        catch (Exception ex) { if (expected == generation) Status?.Invoke("自动填写失败：" + ex.Message); }
        finally { if (fillingGeneration == expected) fillingGeneration = -1; }
    }
    private async Task FillAsync(int expected)
    {
        for (var attempt = 0; attempt <= 80; attempt++) {
            if (expected != generation || finished || profile is null) return;
            var activeProfile = profile;
            var connected = await CampusNetwork.IsAvailableAsync(activeProfile);
            if (expected != generation || finished || View.IsDisposed) return;
            if (!connected) { Stop(); Status?.Invoke("校园网已断开，停止自动填写。"); return; }
            if (!Uri.TryCreate(View.CoreWebView2.Source, UriKind.Absolute, out var page) || !IsSchoolPage(activeProfile, page)) {
                Status?.Invoke("当前页面与保存的认证地址不在同一学校网址下（https 认证地址才会自动登录），请核实后更新设置。"); return;
            }
            if (profile.Username.Length == 0) {
                RequiresInteraction = true; Status?.Invoke("请在设置中填写上网账号密码，或直接在网页中手动登录。"); return;
            }
            // The password is decrypted off the logon start-up path; wait for it instead
            // of reporting an empty field.
            if (password.Length == 0 && passwordPending is not null && attempt < 60) { await Task.Delay(250); continue; }
            if (password.Length == 0) {
                RequiresInteraction = true; Status?.Invoke("未读取到保存的密码，请在设置中重新填写并保存。"); return;
            }
            var result = JsonSerializer.Deserialize<string>(await View.CoreWebView2.ExecuteScriptAsync(PortalScript.Build(profile, password)));
            if (expected != generation) return;
            switch (result) {
                case "authenticated":
                    // Internet access is confirmed only by Connectivity.CheckAsync.
                    ConfirmAuthentication(); return;
                case "authentication-rejected":
                    RequiresInteraction = true; finished = true; password = "";
                    Status?.Invoke("学校返回登录失败，请查看网页提示并检查账号、密码和服务选项。"); return;
                case "submitted": case "already-submitted":
                    finished = true; password = ""; RequiresInteraction = true;
                    authenticationDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
                    StartupTiming.Mark(result == "submitted" ? "login submitted" : "login page was already submitted");
                    Status?.Invoke("已触发登录，正在核验学校返回结果和已登录页面…");
                    Submitted?.Invoke(); await ObserveAuthenticationAsync(); return;
                case "waiting-submit" when attempt < 80: await Task.Delay(150); continue;
                case "waiting-submit": case "submission-unconfirmed":
                    RequiresInteraction = true; finished = true; password = "";
                    Status?.Invoke("学校登录函数未完成提交，请查看网页提示，补充信息后重试。"); return;
                case "waiting-ready" when attempt < 80:
                case "missing-submit" when attempt < 80: await Task.Delay(150); continue;
                case "missing-service" when attempt < 80: await Task.Delay(150); continue;
                case "missing-service":
                    RequiresInteraction = true; finished = true; password = ""; Status?.Invoke("未找到或未能选中填写的服务选项，请检查完整名称或在网页中手动选择。网页没有选项时，请清空设置中的服务选项。"); return;
                case "filled":
                    RequiresInteraction = true; finished = true; password = ""; Status?.Invoke("账号密码已填写，请在网页中手动点击登录。"); return;
                case "missing-submit":
                    RequiresInteraction = true; finished = true; password = ""; Status?.Invoke("账号密码已填写，未识别可用的认证按钮，请手动点击或检查按钮选择器。"); return;
                case "missing-fields" when attempt < 80: await Task.Delay(150); continue;
                case "waiting-agreement" when attempt < 80: await Task.Delay(150); continue;
                case "waiting-agreement": case "agreement-required":
                    RequiresInteraction = true; finished = true; password = "";
                    Status?.Invoke("网页必选勾选项尚未确认，请在认证页完成协议阅读或手动勾选后重试。"); return;
                case "manual-required": RequiresInteraction = true; finished = true; password = ""; Status?.Invoke("学校要求验证码或运营商信息，请在认证页补充。"); return;
                case "waiting-ready": finished = true; password = ""; Status?.Invoke("学校认证参数尚未就绪，请检查页面后重试。"); return;
                case "invalid-selector": RequiresInteraction = true; Status?.Invoke("CSS 选择器无效，请修改高级设置。"); return;
                case "wrong-origin": RequiresInteraction = true; Status?.Invoke("认证地址不匹配，已阻止填写。"); return;
                default: RequiresInteraction = true; Status?.Invoke("未识别登录表单，请手动登录或设置 CSS 选择器。"); return;
            }
        }
    }
}
