using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CampusConnect;

internal sealed partial class MainForm : Form
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private CampusProfile saved = new();
    private string savedPassword = "";
    private readonly AutoPolicy policy = new(DateTimeOffset.UtcNow);
    private readonly PortalRecovery recovery = new();
    private readonly PortalController portal;
    private WebView2 shell = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 20000 };
    // While the campus network is still missing the heartbeat uses this short interval,
    // so the login starts as soon as Wi-Fi associates. The logon grace bounds how long.
    private static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(20);
    private bool fastProbing;
    // Answers "is the login still alive?" immediately when Windows reports a change.
    private NetworkEvents? networkChange;
    private readonly NotifyIcon tray;
    private readonly StartupRegistration startup = new(Application.ExecutablePath);
    private IReadOnlyList<WifiNetwork> networks = [];
    private string? currentSsid;
    private Guid? wiredInterface;
    private string[] ethernetAdapters = [], broadbandEntries = [];
    private CancellationTokenSource? wiredAttempt;
    private string status = "准备就绪";
    private string portalStatus = "连接校园网后，在这里完成上网认证。";
    private string page = "dashboard";
    private bool pageTransitioning;
    private string lastScan = "";
    private bool busy, joining, checking, exiting, uiReady, permissionNeeded, stateQueued;
    // The school page confirmed this account is logged in; only a disconnect clears it.
    private bool authenticationConfirmed;
    private readonly bool startupLaunched;
    private bool shellFailed;
    private Task<CoreWebView2Environment>? browserEnvironment;
    private Task<(CampusProfile Profile, string? ProtectedPassword)>? configurationLoad;
    // DPAPI can block for seconds right after logon; the password arrives separately.
    private Task<string>? passwordLoad;
    private bool? online;
    private int generation;
    private Rectangle portalBounds;
    private readonly string? verificationPath;

    public MainForm(string? verificationPath = null, bool startInTray = false, Task<CoreWebView2Environment>? warmBrowser = null)
    {
        this.verificationPath = verificationPath;
        startupLaunched = startInTray;
        browserEnvironment = warmBrowser;
        StartupTiming.Mark(startInTray ? "logon launch: window created" : "manual launch: window created");
        portal = new(GetBrowserEnvironmentAsync);
        Text = "校园网助手";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1280, 860);
        MinimumSize = new Size(996, 739);
        StartPosition = FormStartPosition.CenterScreen;
        if (startInTray) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; }
        // Logon launches hide the window; a hidden container gives its browser no usable
        // viewport. The window is parked off-screen instead, so the login page can finish
        // by itself until the user opens the tray window.
        if (startInTray) OffScreen();
        BackColor = Color.White;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        Controls.Add(shell);
        portal.View.Dock = DockStyle.None;
        portal.View.Visible = false;
        Controls.Add(portal.View);
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => RestoreWindow());
        menu.Items.Add("刷新界面", null, (_, _) => RestoreWindow());
        menu.Items.Add("暂停自动连接", null, (_, _) => Pause());
        menu.Items.Add("退出", null, (_, _) => { exiting = true; Close(); });
        tray = new NotifyIcon { Icon = Icon, Text = "校园网助手", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => RestoreWindow();
        FormClosing += (_, e) => {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) {
                e.Cancel = true; OffScreen();
                tray.ShowBalloonTip(2500, "校园网助手", "程序保留在系统托盘，右键图标可暂停或退出。", ToolTipIcon.Info);
            }
        };
        FormClosed += (_, _) => {
            wiredAttempt?.Cancel();
            timer.Stop(); timer.Dispose(); tray.Dispose(); networkChange?.Dispose();
            portal.View.Dispose(); shell.Dispose();
        };
        portal.Status += message => {
            if (portalStatus != message && message != "已经连接成功") Record(message);
            portalStatus = message; UpdatePortalVisibility(); PushState();
        };
        portal.AddressChanged += () => PushState();
        portal.Submitted += async () => {
            // The school page accepted this account: keep it until the network changes.
            authenticationConfirmed = true;
            var expected = generation;
            await Task.Delay(1000);
            if (!IsDisposed && expected == generation) await SafeAsync(() => CheckInternetAsync(false));
        };
        timer.Tick += async (_, _) => await HeartbeatAsync();
        networkChange = new NetworkEvents(() => BeginInvoke((Action)(() => { if (!IsDisposed && uiReady) SendState(); })));
        InitializeBrowserRecovery();
        Activated += (_, _) => { if (uiReady) PushStartup(); };
        Shown += async (_, _) => {
            if (startInTray) OffScreen();
            // Overlap disk/DPAPI work with the WebView2 runtime startup.
            configurationLoad = verificationPath is null ? Task.Run(LocalStore.LoadProfile) : Task.FromResult<(CampusProfile, string?)>((new CampusProfile(), null));
            passwordLoad = PasswordAfterLoadAsync();
            // Connecting must not wait for the embedded UI: a logon launch starts the
            // campus network in the background while WebView2 is still coming up.
            _ = ConnectAfterLoadAsync();
            try { await InitializeShellAsync(); }
            catch (WebView2RuntimeNotFoundException) when (startInTray) {
                // Nobody clicked to reach a logon launch: report it in the tray and keep
                // the background connection running instead of exiting silently.
                shellFailed = true;
                Record("缺少 Microsoft Edge WebView2 组件，界面无法显示，后台连接仍在运行。");
                tray.ShowBalloonTip(5000, "校园网助手", "缺少 Microsoft Edge WebView2 组件。请联网后重新运行安装程序，完成组件安装再打开应用。", ToolTipIcon.Warning);
            }
            catch (WebView2RuntimeNotFoundException) {
                MessageBox.Show(this, "缺少 Microsoft Edge WebView2 组件。请联网后重新运行安装程序，完成组件安装再打开应用。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                exiting = true; Close();
            }
            catch (Exception ex) when (startInTray) {
                shellFailed = true;
                Record("界面加载失败，后台连接仍在运行：" + ex.Message);
            }
            catch (Exception ex) {
                MessageBox.Show(this, "界面启动失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                exiting = true; Close();
            }
        };
    }

    // Moves the window outside the virtual screen instead of hiding it: a hidden
    // WinForms window leaves its WebView2 child without a viewport, and the school page
    // cannot fill or submit the login form without one.
    private void OffScreen()
    {
        if (!IsHandleCreated) return;
        ShowInTaskbar = false;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 860);
        Location = new Point(area.Right + 200, area.Bottom + 200);
    }

    // Waits for the profile, then decrypts the password off the UI thread. The campus
    // login page is opened without waiting for it; the script takes it once it is there.
    private async Task<string> PasswordAfterLoadAsync()
    {
        var protectedPassword = configurationLoad is null ? null : (await configurationLoad).ProtectedPassword;
        if (protectedPassword is null) return "";
        var password = await Task.Run(() => LocalStore.DecryptPassword(protectedPassword));
        if (!IsDisposed) savedPassword = password;
        StartupTiming.Mark("password ready");
        return password;
    }

    private async Task ConnectAfterLoadAsync()
    {
        try {
            if (configurationLoad is null) return;
            var loaded = await configurationLoad;
            if (IsDisposed || configurationLoaded) return;
            saved = loaded.Profile;
            StartupTiming.Mark("configuration loaded");
            if (!saved.Automatic) return;
            // The browser runtime is already loading from Program.Main; make sure the
            // failure is observed here even when the UI never gets that far.
            if (saved.UsesWebPortal) _ = GetBrowserEnvironmentAsync().ContinueWith(_ => { }, TaskScheduler.Default);
            // One attempt right away, then the 20 s heartbeat as soon as the UI is ready.
            // A normal launch arms the heartbeat from the UI; a logon launch must still
            // retry when the embedded UI fails to load.
            _ = RefreshAsync(scan: false);
            if (!startupLaunched) return;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (!IsDisposed && !uiReady && !shellFailed && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(250);
            if (!IsDisposed && !uiReady && IsHandleCreated)
                BeginInvoke((Action)(() => { if (!IsDisposed && !uiReady) timer.Start(); }));
        } catch (Exception ex) {
            Record("读取配置失败：" + ex.Message);
        }
    }

    private Task<CoreWebView2Environment> GetBrowserEnvironmentAsync()
    {
        Directory.CreateDirectory(LocalStore.DirectoryPath);
        if (browserEnvironment is { IsFaulted: true } or { IsCanceled: true }) browserEnvironment = null;
        return browserEnvironment ??= PrewarmBrowserAsync();
    }

    // Called from Program.Main before the window exists, and reused by both browsers.
    internal static async Task<CoreWebView2Environment> PrewarmBrowserAsync()
    {
        Directory.CreateDirectory(LocalStore.DirectoryPath);
        var environment = await CreateEnvironmentAsync();
        StartupTiming.Mark("WebView2 runtime ready");
        return environment;
    }

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        // Prefer a pinned runtime so the system runtime's updater does not compete with
        // the logon start-up; any failure falls back to the default runtime.
        if (LocalStore.ReadRuntimeChannel() is { } channel && FindRuntimeFolder(channel) is { } folder) {
            try {
                var pinned = await CoreWebView2Environment.CreateAsync(folder,
                    Path.Combine(LocalStore.DirectoryPath, "WebView2"), new CoreWebView2EnvironmentOptions("--no-proxy-server"));
                StartupTiming.Mark("WebView2 pinned runtime " + channel);
                return pinned;
            } catch (Exception ex) when (ex is WebView2RuntimeNotFoundException
                                         or System.Runtime.InteropServices.COMException or IOException) { }
        }
        return await CoreWebView2Environment.CreateAsync(null,
            Path.Combine(LocalStore.DirectoryPath, "WebView2"), new CoreWebView2EnvironmentOptions("--no-proxy-server"));
    }

    // Evergreen WebView2 keeps every installed version side by side; the newest is used.
    private static string? FindRuntimeFolder(string channel)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft", "EdgeWebView", "Application");
        try {
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root).Where(d => Version.TryParse(Path.GetFileName(d), out _))
                    .OrderByDescending(d => Version.Parse(Path.GetFileName(d))).FirstOrDefault()
                : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task InitializeShellAsync()
    {
        var view = shell;
        var environment = await GetBrowserEnvironmentAsync();
        if (IsDisposed || view.IsDisposed || view != shell) return;
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.IsInPrivateModeEnabled = true;
        await view.EnsureCoreWebView2Async(environment, options);
        if (IsDisposed || view.IsDisposed || view != shell) return;
        var core = view.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.NavigationStarting += (_, e) => { if (!DesktopBridge.IsTrustedSource(e.Uri)) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("CampusConnect.Desktop.html")
            ?? throw new InvalidOperationException("缺少内置界面。");
        using var reader = new StreamReader(stream);
        var bytes = Encoding.UTF8.GetBytes(await reader.ReadToEndAsync());
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => {
            var trusted = DesktopBridge.IsTrustedSource(e.Request.Uri);
            e.Response = environment.CreateWebResourceResponse(
                new MemoryStream(trusted ? bytes : []), trusted ? 200 : 403, trusted ? "OK" : "Forbidden",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
        };
        core.WebMessageReceived += async (_, e) => {
            // This bridge belongs ONLY to the embedded application UI, never the school browser.
            if (view != shell || !DesktopBridge.IsTrustedSource(e.Source) || e.WebMessageAsJson.Length > 65536) return;
            await SafeAsync(async () => {
                using var document = JsonDocument.Parse(e.WebMessageAsJson);
                await HandleCommandAsync(document.RootElement);
            });
        };
        core.ProcessFailed += (_, e) => {
            if (view == shell) OnShellProcessFailed(e.ProcessFailedKind);
        };
        core.Navigate(DesktopBridge.Uri);
    }

    private async Task HandleCommandAsync(JsonElement message)
    {
        if (!message.TryGetProperty("command", out var property)) return;
        switch (property.GetString()) {
            case "ready":
                if (uiReady) break;
                var firstReady = !configurationLoaded;
                try { if (firstReady && configurationLoad is not null) saved = (await configurationLoad).Profile; }
                catch (Exception ex) { Record("无法读取配置，请重新填写并保存：" + ex.Message); }
                configurationLoaded = true;
                uiReady = true; PushProfile(); PushStartup(); SendState();
                Post(new { kind = "restore", page, snapshot = shellSnapshot });
                if (!firstReady) break;
                if (verificationPath is not null) {
                    var result = await shell.CoreWebView2.ExecuteScriptAsync("JSON.stringify({pageCount:document.querySelectorAll('.nav button').length,title:document.title,passwordMasked:document.getElementById('password').type==='password',startupToggle:!!document.getElementById('startup'),byYe:document.querySelector('.author').textContent})");
                    var snapshot = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Deserialize<string>(result) ?? "{}") ?? [];
                    snapshot["mainWindowVisible"] = JsonSerializer.SerializeToElement(Visible);
                    snapshot["showInTaskbar"] = JsonSerializer.SerializeToElement(ShowInTaskbar);
                    File.WriteAllText(verificationPath, JsonSerializer.Serialize(snapshot));
                    exiting = true; Close(); return;
                }
                timer.Start();
                // Connect immediately from the WLAN cache when the target is already visible.
                await RefreshAsync(scan: false);
                if (!saved.IsWired && currentSsid != saved.Ssid) await RefreshAsync(); break;
            case "hydrated":
                shellReady?.TrySetResult(true);
                break;
            case "draft":
                if (uiReady && message.TryGetProperty("snapshot", out var snapshotData) && snapshotData.ValueKind == JsonValueKind.Object)
                    shellSnapshot = snapshotData.Clone();
                break;
            case "page":
                var next = message.GetProperty("page").GetString();
                if (next is "dashboard" or "settings" or "portal") {
                    page = next;
                    pageTransitioning = message.TryGetProperty("transitioning", out var transition) && transition.ValueKind == JsonValueKind.True;
                    UpdatePortalVisibility();
                }
                break;
            case "portalBounds":
                var requestedPage = message.GetProperty("page").GetString();
                if (requestedPage != page) break;
                var bounds = DesktopBridge.ScaleBounds(message, ClientSize.Width, ClientSize.Height);
                if (bounds is { } value) portalBounds = new(value.X, value.Y, value.Width, value.Height);
                UpdatePortalVisibility(); break;
            case "refresh": await RefreshAsync(); break;
            case "connect": await ManualConnectAsync(); break;
            case "save": SaveFromMessage(message); break;
            case "setAutomatic":
                if (!message.GetProperty("enabled").GetBoolean()) Pause();
                else SaveFromMessage(message, enable: true);
                break;
            case "openPortal": await OpenPortalAsync(); break;
            case "discoverPortal": await OpenPortalAsync(discover: true); break;
            case "stopPortal": StopAuthentication(); portalStatus = "认证已暂停。"; PushState(); UpdatePortalVisibility(); break;
            case "checkInternet": await CheckInternetAsync(false); break;
            case "adoptAddress":
                var url = portal.Address;
                if (CampusProfile.Origin(url) is null) throw new InvalidOperationException("当前没有可保存的认证网址。");
                Post(new { kind = "address", address = url });
                Record("请确认网址属于学校，点击保存后生效。");
                break;
            case "setStartup":
                try {
                    startup.SetEnabled(message.GetProperty("enabled").GetBoolean());
                    Record(startup.IsEnabled() ? "开机自启已开启，登录 Windows 后在托盘运行。" : "开机自启已关闭。");
                } finally { PushStartup(); }
                break;
            case "location": OpenSettings("ms-settings:privacy-location"); break;
            case "wifiSettings": OpenSettings("ms-settings:network-wifi"); break;
            case "ethernetSettings": OpenSettings("ms-settings:network-ethernet"); break;
            case "broadbandSettings": OpenSettings("ms-settings:network-dialup"); break;
            case "networkConnections": Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "control.exe")) { Arguments = "ncpa.cpl", UseShellExecute = true }); break;
            case "chooseAuthClient":
                using (var picker = new OpenFileDialog { Title = "选择学校认证客户端", Filter = "应用程序 (*.exe)|*.exe", CheckFileExists = true }) {
                    if (picker.ShowDialog(this) == DialogResult.OK) Post(new { kind = "authClient", path = picker.FileName });
                }
                break;
        }
    }

    private void UpdatePortalVisibility()
    {
        if (IsDisposed) return;
        var show = uiReady && page == "portal" && !pageTransitioning && portal.IsActive && portalBounds.Width > 0 && portalBounds.Height > 0;
        if (show) {
            if (portal.View.Bounds != portalBounds) portal.View.Bounds = portalBounds;
            if (!portal.View.Visible) portal.View.BringToFront();
        }
        if (portal.View.Visible != show) {
            portal.View.Visible = show;
            if (show) portal.ResumeView();
        }
    }
    private void Post(object message)
    {
        if (!uiReady || IsDisposed || Disposing) return;
        try {
            if (shell.CoreWebView2 is { } core && DesktopBridge.IsTrustedSource(core.Source))
                core.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
        } catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) {
            RequestShellRecovery();
        }
    }
    private void PushStartup()
    {
        try { Post(new { kind = "startup", enabled = startup.IsEnabled(), available = true }); }
        catch (Exception ex) { Post(new { kind = "startup", enabled = false, available = false, error = "无法读取启动项：" + ex.Message }); }
    }
    private void PushProfile() => Post(new { kind = "profile", profile = saved, password = savedPassword });
    private void PushState()
    {
        if (!uiReady || IsDisposed || Disposing || !IsHandleCreated || stateQueued) return;
        // Several updates in one command should render a single final snapshot.
        stateQueued = true;
        BeginInvoke((Action)(() => {
            stateQueued = false;
            if (!IsDisposed && !Disposing) SendState();
        }));
    }
    private void SendState()
    {
        var connectionLabel = saved.ConnectionLabel;
        Text = "校园网助手 · " + connectionLabel;
        tray.Text = Text;
        var connected = saved.MatchesNetwork(currentSsid, saved.IsWired && CampusNetwork.ReadWiredInterface(saved) is not null);
        var title = saved.IsWired ? (connected ? (online == true ? "有线校园网已连接 · 可上网" : online == false ? "有线网络已连接 · 未联网" : "有线网络已连接 · 正在确认上网状态") : "有线网络未连接，请插好网线") : joining ? "正在连接校园网" : permissionNeeded ? "等待 Wi-Fi 访问授权" :
            connected ? (online == true ? "校园网已连接 · 可上网" : online == false ? "Wi-Fi 已连接 · 未联网" : "Wi-Fi 已连接 · 正在确认上网状态") :
            currentSsid is null ? "当前未连接 Wi-Fi" : "已连接 " + currentSsid;
        if (saved.IsWired && saved.UsesSystemAuthentication) title = joining ? "正在连接 · " + saved.ConnectionLabel
            : wiredInterface is null ? "有线网络未连接，请插好网线"
            : online == true && connected ? saved.ConnectionLabel + " · 可上网" : saved.ConnectionLabel + " · 等待认证或联网确认";
        Post(new { kind = "state", state = new {
            target = saved.IsWired ? "有线网络" : saved.Ssid, wiredMode = saved.IsWired, currentSsid, campusConnected = connected, internetOnline = connected && online == true, connectionTitle = title,
            wiredAuth = saved.WiredAuth, connectionLabel = saved.ConnectionLabel, webPortalEnabled = saved.UsesWebPortal,
            ethernetAdapters, broadbandEntries,
            automatic = saved.Automatic, busy, permissionNeeded, lastScan, status,
            portalStatus, portalAddress = portal.Address,
            networks = networks.DistinctBy(n => n.Ssid).Select(n => new { n.Ssid, n.Signal, n.Secure }).ToArray()
        }});
    }
    private void Navigate(string target) { page = target; UpdatePortalVisibility(); Post(new { kind = "page", page = target }); }
    private void SaveFromMessage(JsonElement message, bool? enable = null)
    {
        if (busy) { Record("正在扫描或连接，请稍候再保存。"); PushState(); return; }
        var draft = message.GetProperty("profile").Deserialize<CampusProfile>(JsonOptions)
            ?? throw new InvalidOperationException("设置无效。");
        draft = draft with { Ssid = draft.Ssid.Trim(), PortalUrl = draft.PortalUrl.Trim(), Username = draft.Username.Trim(),
            EthernetName = draft.EthernetName.Trim(), BroadbandName = draft.BroadbandName.Trim() };
        if (enable is not null) draft = draft with { Automatic = enable.Value };
        var secret = message.GetProperty("password").GetString() ?? "";
        draft.Validate();
        if (draft.Automatic && draft.UsesWebPortal && (draft.Username.Length == 0 || secret.Length == 0))
            throw new InvalidOperationException("请先填写上网账号和密码，再开启自动连接。");
        LocalStore.Save(draft, secret);
        wiredAttempt?.Cancel();
        generation++; saved = draft; savedPassword = secret; policy.Reset(); recovery.Reset(); online = null; authenticationConfirmed = false;
        currentSsid = null; wiredInterface = null; permissionNeeded = false; networks = [];
        StopAuthentication(); UpdatePortalVisibility(); shellSnapshot = null; PushProfile();
        Record(saved.Automatic ? "设置已保存，自动连接已开启。" : "设置已保存。");
        _ = RefreshAsync(scan: false);
    }
    private void Pause()
    {
        wiredAttempt?.Cancel();
        generation++; recovery.Reset(); saved = saved with { Automatic = false }; StopAuthentication(); UpdatePortalVisibility();
        try { LocalStore.Save(saved, savedPassword); Record("自动连接已暂停。"); }
        catch (Exception ex) { Record("本次运行已暂停，但保存设置失败：" + ex.Message); }
    }
    private void Record(string message)
    {
        if (IsDisposed) return;
        status = message;
        PushState();
    }
    private async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) {
            if (ex.Message.Contains("定位", StringComparison.Ordinal)) permissionNeeded = true;
            Record(ex.Message);
        }
    }
    // One heartbeat tick. Scanning the WLAN cache is cheap; a full scan is not, so the
    // fast interval is only used while the campus network is absent from the cache and
    // the logon grace covers it.
    private async Task HeartbeatAsync()
    {
        var missing = !saved.IsWired && saved.Ssid.Length > 0 && currentSsid != saved.Ssid;
        if (missing && policy.MayWaitLonger(DateTimeOffset.UtcNow)) {
            if (!fastProbing) { fastProbing = true; timer.Interval = (int)FastInterval.TotalMilliseconds; }
        } else if (fastProbing) {
            fastProbing = false; timer.Interval = (int)SlowInterval.TotalMilliseconds;
        }
        // A scan during the fast window would cost more than the beat itself.
        await RefreshAsync(scan: !fastProbing && missing);
    }

    private async Task RefreshAsync(bool scan = true)
    {
        if (busy || IsDisposed) return;
        busy = true; PushState();
        try {
            await SafeAsync(async () => {
                var expected = generation;
                // At logon the network stack is still starting; record when the wait ends.
                if (saved.Automatic) StartupTiming.Mark("network scan started");
                var choices = await Task.Run(() => (
                    Ethernet: CampusNetwork.ReadLinks().Where(n => CampusNetwork.IsWiredInterface(n.Type, System.Net.NetworkInformation.OperationalStatus.Up)).Select(n => n.Name).ToArray(),
                    Broadband: WiredAuthentication.ReadBroadbandEntries().Select(e => e.Name).ToArray()));
                if (IsDisposed || expected != generation) return;
                ethernetAdapters = choices.Ethernet; broadbandEntries = choices.Broadband;
                if (saved.IsWired) {
                    RefreshWiredConnection();
                    if (wiredInterface is not null) await ConnectWiredAsync(manual: false);
                    return;
                }
                var snapshot = await WifiService.ReadSnapshotAsync(saved.Ssid, scan);
                if (IsDisposed || expected != generation) return;
                permissionNeeded = false; networks = snapshot.Networks; lastScan = DateTime.Now.ToString("HH:mm");
                if (currentSsid != snapshot.Current) {
                    currentSsid = snapshot.Current; expected = ++generation; recovery.Reset(); online = null; authenticationConfirmed = false;
                    StopAuthentication(); UpdatePortalVisibility();
                    Record(currentSsid is null ? "Wi-Fi 连接已断开。" : "当前连接：" + currentSsid);
                    // Lost the campus network: look for it quickly while the grace allows.
                    if (currentSsid != saved.Ssid && policy.MayWaitLonger(DateTimeOffset.UtcNow) && !fastProbing) {
                        fastProbing = true; timer.Interval = (int)FastInterval.TotalMilliseconds;
                    }
                }
                if (snapshot.Current == saved.Ssid) {
                    StartupTiming.Mark("campus Wi-Fi ready (scan=" + scan + ")");
                    // Associated: from here the 20 second resting interval is enough.
                    if (fastProbing) { fastProbing = false; timer.Interval = (int)SlowInterval.TotalMilliseconds; }
                    _ = SafeAsync(() => CheckInternetAsync(saved.Automatic));
                } else if (policy.ShouldJoin(saved.Automatic, saved.Ssid, snapshot.Current, snapshot.Networks.Select(n => n.Ssid), false, DateTimeOffset.UtcNow)) {
                    // After logon the wireless adapter is often still starting and its cache
                    // is empty; look again shortly instead of staying silent for a minute.
                    if (!snapshot.Networks.Any(n => n.Ssid == saved.Ssid)) {
                        if (!policy.MayWaitLonger(DateTimeOffset.UtcNow)) { Record(saved.Ssid + " 不在附近，将继续按间隔自动重试。"); return; }
                        Record("暂未发现 " + saved.Ssid + "，正在等待 Wi-Fi 就绪…");
                        await Task.Delay(TimeSpan.FromSeconds(3));
                        if (IsDisposed || expected != generation) return;
                        var retry = await WifiService.ReadSnapshotAsync(saved.Ssid, scan);
                        if (IsDisposed || expected != generation) return;
                        networks = retry.Networks;
                        if (!retry.Networks.Any(n => n.Ssid == saved.Ssid)) return;
                        snapshot = retry;
                    }
                    await ConnectAsync(snapshot, expected);
                }
            });
        } finally { busy = false; PushState(); }
    }
    private void RefreshWiredConnection()
    {
        var current = CampusNetwork.ReadPhysicalLink(saved)?.Id;
        permissionNeeded = false; currentSsid = null; networks = []; lastScan = DateTime.Now.ToString("HH:mm");
        if (wiredInterface != current) {
            wiredInterface = current; generation++; recovery.Reset(); online = null; authenticationConfirmed = false;
            StopAuthentication(); UpdatePortalVisibility();
        }
        if (current is null) Record("有线网络未连接，请插好网线并在系统中启用以太网。");
    }
    private async Task ManualConnectAsync()
    {
        if (busy) { Record("正在扫描或连接，请稍候。"); return; }
        busy = true; PushState();
        try {
            policy.Reset();
            if (saved.IsWired) {
                RefreshWiredConnection();
                if (wiredInterface is null) return;
                await ConnectWiredAsync(manual: true);
                return;
            }
            var expected = generation;
            Record("正在寻找已保存的校园 Wi-Fi…");
            StartupTiming.Mark("manual connect requested");
            var snapshot = await WifiService.ReadSnapshotAsync(saved.Ssid);
            if (expected == generation && !IsDisposed) await ConnectAsync(snapshot, expected, manual: true);
        } finally { busy = false; PushState(); }
    }
    private async Task ConnectWiredAsync(bool manual)
    {
        var expected = generation;
        var active = saved;
        if (active.UsesSystemAuthentication && (manual || policy.ShouldPrepareWired(active.Automatic, online != true, joining, DateTimeOffset.UtcNow))) {
            if (active.WiredAuth != "pppoe" || !await CampusNetwork.IsAvailableAsync(active)) {
                using var cancellation = new CancellationTokenSource();
                wiredAttempt = cancellation;
                joining = true; policy.Begin(DateTimeOffset.UtcNow); PushState();
                try {
                    Record("正在连接 · " + active.ConnectionLabel);
                    var result = await WiredAuthentication.ConnectAsync(active, CampusNetwork.ReadPhysicalLink(active)?.Name ?? "", cancellation.Token);
                    if (expected != generation || IsDisposed) return;
                    Record(result);
                } catch (OperationCanceledException) { return; }
                finally { if (wiredAttempt == cancellation) wiredAttempt = null; joining = false; PushState(); }
            }
        }
        if (expected != generation || IsDisposed) return;
        if (manual && active.UsesWebPortal) await OpenPortalAsync();
        await CheckInternetAsync(!manual && active.Automatic);
    }
    private async Task ConnectAsync(WifiSnapshot snapshot, int expected, bool manual = false)
    {
        policy.Begin(DateTimeOffset.UtcNow); joining = true; PushState();
        try { await WifiService.JoinAsync(saved, snapshot); }
        catch (Exception ex) {
            throw new InvalidOperationException(ex.Message + (saved.Automatic
                ? " 将继续自动重试，不限制次数。" : ""), ex);
        } finally { joining = false; PushState(); }
        if (expected != generation || IsDisposed) return;
        currentSsid = saved.Ssid; recovery.Reset(); online = null; permissionNeeded = false; authenticationConfirmed = false;
        Record("已连接校园 Wi-Fi，正在检查上网状态…");
        if (manual && saved.UsesWebPortal) await OpenPortalAsync();
        await CheckInternetAsync(!manual && saved.Automatic);
    }
    private async Task CheckInternetAsync(bool openIfOffline)
    {
        if (checking) return;
        checking = true;
        try {
            var expected = generation;
            var activeProfile = saved;
            var connected = await CampusNetwork.IsAvailableAsync(activeProfile);
            if (expected != generation || IsDisposed) return;
            if (!connected) {
                online = false; recovery.Observe(InternetStatus.Unavailable);
                Record(activeProfile.IsWired ? activeProfile.WiredAuth == "pppoe" ? "PPPoE 尚未建立连接，请检查 Windows 宽带连接设置。" : "请先插好网线并启用有线网络。" : "请先连接校园 Wi-Fi。"); return;
            }
            // Start the first login before waiting for external internet probes. This must
            // never depend on the window being visible or activated: the school page is
            // rendered in the portal container and submits itself.
            if (openIfOffline && online is null && !portal.RequiresInteraction &&
                recovery.ShouldAuthenticate(saved.Automatic, saved.UsesWebPortal, DateTimeOffset.UtcNow)) {
                Record("正在登录校园网…");
                StartupTiming.Mark("opening campus login page");
                // Releasing the guard first: the login page reports back through the
                // Submitted event, and a probe that waited for it must not be swallowed.
                checking = false;
                await OpenPortalAsync(showPage: page == "portal");
                StartupTiming.Mark("campus login page opened");
                if (checking) return;
                checking = true;
                if (expected != generation || IsDisposed) return;
            }
            var adapter = await CampusNetwork.ReadInterfaceAsync(activeProfile);
            if (expected != generation || IsDisposed) return;
            var result = await Connectivity.CheckAsync(adapter);
            if (expected != generation || IsDisposed) return;
            connected = await CampusNetwork.IsAvailableAsync(activeProfile);
            if (expected != generation || IsDisposed || !connected) return;
            online = result == InternetStatus.Online;
            recovery.Observe(result);
            if (result == InternetStatus.Online) {
                StartupTiming.Mark("INTERNET REACHABLE");
                // Only a logged-in page can be recorded as a success; otherwise the next
                // heartbeat would mistake "ping slow" for "login lost" and log in again.
                portal.ConfirmInternetAccess(authenticationConfirmed); status = "";
            } else portal.ReportInternetUnavailable();
            // A confirmed account is not signed out by a failed ping: the school session
            // survives, so the login page is only reopened when the account is not confirmed.
            if (!authenticationConfirmed && result != InternetStatus.Online) {
                Record("百度 ping 不通，判定未联网。");
                if (saved.Automatic && saved.AutoSubmit && !portal.RequiresInteraction && !portal.IsAuthenticating &&
                    recovery.ShouldAuthenticate(saved.Automatic, saved.UsesWebPortal, DateTimeOffset.UtcNow)) {
                    Record("百度 ping 不通，正在重新连接校园网认证…");
                    await OpenPortalAsync(showPage: false);
                }
            }
        } finally { checking = false; PushState(); }
    }
    private void StopAuthentication()
    {
        portal.Stop();
    }
    private async Task OpenPortalAsync(bool discover = false, bool showPage = true)
    {
        if (saved.UsesSystemAuthentication) { Record("当前使用系统认证，请在网络概览点击立即连接。"); return; }
        if (!saved.UsesWebPortal && !discover) { Record("网页认证已关闭，请在设置中开启并保存。"); return; }
        recovery.Begin(DateTimeOffset.UtcNow);
        if (showPage) Navigate("portal");
        await portal.OpenAsync(saved, savedPassword, discover, savedPassword.Length == 0 ? passwordLoad : null);
        StartupTiming.Mark(discover ? "portal discovered" : "portal controller opened");
        UpdatePortalVisibility(); PushState();
    }
    private static void OpenSettings(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    private void RestoreWindow() {
        ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal;
        // A window parked by OffScreen() overlaps no display at all.
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Bounds))) CenterToScreen();
        Activate();
        RequestShellRecovery(userRequested: true);
    }
}
