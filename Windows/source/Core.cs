using System.Text;
using System.Text.Json;

namespace CampusConnect;

public sealed record CampusProfile
{
    public string Ssid { get; init; } = "HNPI-STU";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsWired => string.IsNullOrWhiteSpace(Ssid);
    public bool MatchesNetwork(string? currentSsid, bool wiredAvailable)
        => IsWired ? wiredAvailable : currentSsid == Ssid;
    public string WiredAuth { get; init; } = "web";
    public string EthernetName { get; init; } = "";
    public string BroadbandName { get; init; } = "";
    public string AuthenticationClient { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesSystemAuthentication => IsWired && WiredAuth != "web";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesWebPortal => PortalEnabled && !UsesSystemAuthentication;
    [System.Text.Json.Serialization.JsonIgnore]
    public string ConnectionLabel => !IsWired ? "Wi-Fi" : WiredAuth switch {
        "pppoe" => "有线 · PPPoE 拨号", "dot1x" => "有线 · 802.1X", "client" => "有线 · 专用客户端", _ => "有线 · 网页认证"
    };
    public bool UseSavedWifiProfile { get; init; }
    public bool PortalEnabled { get; init; } = true;
    public string PortalUrl { get; init; } = "http://192.168.0.123";
    public string Username { get; init; } = "";
    public string ServiceOption { get; init; } = "";
    public string UserSelector { get; init; } = "";
    public string PasswordSelector { get; init; } = "";
    public string SubmitSelector { get; init; } = "";
    public bool AutoSubmit { get; init; } = true;
    public bool Automatic { get; init; }

    public static string? Origin(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    // Campus gateways hand out per-session addresses (wlanuserip, wlanacname, ...)
    // and redirect elsewhere on the same host after a reboot. The school is therefore
    // identified by scheme + host + port; the exact page is not part of the identity.
    // Only the hostname may change between pages, never the scheme or the port.
    public static string? Site(string? address)
    {
        if (Origin(address) is not { } origin || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return null;
        return uri.Host.Length == 0 ? null : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    public static bool SameSite(string? address, string? other)
        => address is not null && other is not null && Site(address) is { } site && site == Site(other);

    // Credentials are only ever typed into or submitted from a page the user configured.
    // Plain http could hand a captured page the account, so automatic login needs https.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AllowsAutomaticLogin => PortalEnabled && Origin(PortalUrl) is { } origin
        && origin.StartsWith("https://", StringComparison.Ordinal);

    public void Validate()
    {
        if (!IsWired && Encoding.UTF8.GetByteCount(Ssid) > 32)
            throw new InvalidOperationException("Wi-Fi 名称最多为 32 字节；有线模式请留空。");
        if (WiredAuth is not ("web" or "pppoe" or "dot1x" or "client"))
            throw new InvalidOperationException("请选择有效的有线认证方式。");
        if (IsWired && WiredAuth == "pppoe" && (string.IsNullOrWhiteSpace(BroadbandName) || BroadbandName.Any(char.IsControl)))
            throw new InvalidOperationException("请填写或选择 Windows 中已配置的 PPPoE 宽带连接名称。");
        if (IsWired && WiredAuth == "client" && (string.IsNullOrWhiteSpace(AuthenticationClient) || !AuthenticationClient.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("请先选择学校认证客户端的 EXE 文件。");
        if (EthernetName.Any(char.IsControl)) throw new InvalidOperationException("以太网接口名称无效。");
        if (UsesWebPortal && Origin(PortalUrl) is null)
            throw new InvalidOperationException("请输入 http:// 或 https:// 开头的认证地址，不能在地址中包含账号密码。");
    }
}

public sealed class AutoPolicy
{
    // At logon the adapter can take a while to finish starting; the network cache is
    // empty until then. Retry quickly during this window instead of the resting interval.
    public static readonly TimeSpan LogonGrace = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan GraceInterval = TimeSpan.FromSeconds(5);
    private readonly DateTimeOffset? startedAt;
    public AutoPolicy(DateTimeOffset? startedAt = null) => this.startedAt = startedAt;
    public DateTimeOffset LastAttempt { get; private set; } = DateTimeOffset.MinValue;
    public bool ShouldJoin(bool enabled, string target, string? current, IEnumerable<string> visible, bool busy, DateTimeOffset now)
        => enabled && target.Length > 0 && current != target && visible.Contains(target, StringComparer.Ordinal)
            && !busy && now - LastAttempt >= (InGrace(now) ? GraceInterval : TimeSpan.FromSeconds(60));
    // The target is not visible yet: still a reason to look again, never a failure.
    public bool MayWaitLonger(DateTimeOffset now) => InGrace(now);
    public bool ShouldPrepareWired(bool enabled, bool needed, bool busy, DateTimeOffset now)
        => enabled && needed && !busy && now - LastAttempt >= (InGrace(now) ? GraceInterval : TimeSpan.FromSeconds(60));
    public void Begin(DateTimeOffset now) => LastAttempt = now;
    public void Reset() { LastAttempt = DateTimeOffset.MinValue; }
    private bool InGrace(DateTimeOffset now) => startedAt is { } start && now - start < LogonGrace;
}

public enum InternetStatus { Online, Unavailable }
public sealed class PortalRecovery
{
    public int Failures { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset LastAttempt { get; private set; } = DateTimeOffset.MinValue;
    public InternetStatus State { get; private set; } = InternetStatus.Unavailable;
    public TimeSpan RetryDelay => TimeSpan.FromSeconds(Math.Min(300, 60 * Math.Pow(2, Math.Max(0, Attempts - 1))));
    public void Observe(InternetStatus result)
    {
        if (result == InternetStatus.Online) Reset();
        else Failures = Math.Min(2, Failures + 1);
        State = result;
    }
    public bool ShouldAuthenticate(bool enabled, bool portalEnabled, DateTimeOffset now)
        => enabled && portalEnabled && State != InternetStatus.Online && ((Attempts == 0 && Failures == 0) || Failures >= 1)
            && now - LastAttempt >= RetryDelay;

    public void Begin(DateTimeOffset now) { LastAttempt = now; Attempts = Math.Min(4, Attempts + 1); }
    public void Reset() { Failures = 0; Attempts = 0; LastAttempt = DateTimeOffset.MinValue; State = InternetStatus.Unavailable; }
}

public static class PortalScript
{
    public static string Build(CampusProfile profile, string password, bool statusOnly = false)
    {
        var origin = CampusProfile.Origin(profile.PortalUrl) ?? throw new InvalidOperationException("认证地址无效。");
        var payload = JsonSerializer.Serialize(new {
            origin, username = profile.Username, password, serviceOption = profile.ServiceOption, userSelector = profile.UserSelector,
            passwordSelector = profile.PasswordSelector, submitSelector = profile.SubmitSelector, submit = profile.AutoSubmit, confirmSubmit = true, statusOnly
        });
        using var stream = typeof(PortalScript).Assembly.GetManifestResourceStream("CampusConnect.PortalTemplate.js")
            ?? throw new InvalidOperationException("缺少认证脚本。");
        using var reader = new StreamReader(stream);
        using var agreementStream = typeof(PortalScript).Assembly.GetManifestResourceStream("CampusConnect.PortalAgreements.js")
            ?? throw new InvalidOperationException("缺少登录勾选脚本。");
        using var agreementReader = new StreamReader(agreementStream);
        return reader.ReadToEnd().Replace("/*CAMPUS_CONFIG*/", payload, StringComparison.Ordinal)
            .Replace("  /*CAMPUS_AGREEMENTS*/", agreementReader.ReadToEnd().TrimEnd(), StringComparison.Ordinal);
    }
}
