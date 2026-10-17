# CampusConnect 开机无感自动连接 — 变更说明

> 基于 CampusConnect 2.0.0（Windows 版）源码修改
> 日期：2026-10-18
> 适用环境：Windows 11 家庭版 26030 / 中国海洋大学 OUC-WIFI（`xha.ouc.edu.cn` 网页认证）

---

## 一、改前的问题

开机自启后，程序虽然会连接校园 Wi-Fi，但**必须手动点「立即连接」按钮**才能完成上网认证。表现为：

| 现象 | 原因 |
|---|---|
| 开机后停在托盘，不自动认证 | 首次认证只在内嵌界面完全加载并可见后才发起，且要求窗口处于激活状态 |
| 即使打开了窗口也得点按钮 | 内嵌认证页容器的可见性依赖「上网认证」页面被打开 |
| 认证地址一变就失效 | 学校门户地址带 `wlanuserip=...` 会话参数，重启后参数变化被判定为「非学校地址」而拒绝填写 |
| 成功一次后又反复登录 | 认证页成功后状态被错误重置，每 20 秒心跳都重新打开登录页并重复提交 |
| 开机后要等一分多钟 | 登录阶段启动项过多（34 秒才出桌面），且程序排在启动队列里 |

---

## 二、改动清单

### 源码改动（`Windows\source`）

| 文件 | 性质 | 改动要点 |
|---|---|---|
| `Core.cs` | 修改 | 新增 `Site()` / `SameSite()` / `AllowsAutomaticLogin`；`AutoPolicy` 增加开机宽限窗口 |
| `PortalController.cs` | 修改 | 校址匹配改为「同主机」；认证成功后进入休息态；支持密码延后到达 |
| `MainForm.cs` | 修改 | 启动即连接、窗口离屏、自适应心跳、DPAPI 延迟解密、WebView2 预热 |
| `LocalStore.cs` | 修改 | 配置与密码分离加载；新增 `settings.json` 运行方式 |
| `Program.cs` | 修改 | `--timing` 计时开关；浏览器运行时提前预热 |
| `StartupTiming.cs` | **新增** | 分阶段计时日志 |
| `NetworkEvents.cs` | **新增** | 网络变化事件监听 |
| `Windows\tests\Program.cs` | 修改 | 新增 16 条断言覆盖地址匹配与开机宽限 |

---

## 三、逐项说明

### 1. 开机无感连接（不再点「立即连接」）

**`MainForm.cs`**

- `Shown` 事件中新增 `ConnectAfterLoadAsync()`：配置一读完就开始连接，**不再等待 WebView2 界面加载完成**。
- 登录启动（`--startup`）时若内嵌界面加载失败，改为记录到托盘状态并继续保持后台连接，而不是弹窗退出。
- `CheckInternetAsync()` 中的首次认证不再要求窗口可见或激活。

**`PortalController.cs`**

- 认证页在隐藏容器中仍完整渲染并自动填写、提交。
- 认证成功后 `RequiresInteraction = true`、`IsActive = false`，页面进入「休息态」，后续心跳不会重复打开。

### 2. 认证地址按「同一主机」匹配

**`Core.cs`**

```csharp
// 学校网址按 协议 + 主机 + 端口 识别，忽略页面与会话参数
public static string? Site(string? address)      // 例如 https://xha.ouc.edu.cn:443
public static bool SameSite(string? a, string? b)
public bool AllowsAutomaticLogin                  // 仅 https 允许后台自动登录
```

**`PortalController.cs`**

- `IsSchoolPage()`：同主机的任意页面（含 `wlanuserip` 变化后的跳转）均视为学校页面。
- 跳转到**同一主机**的其他地址时，自动重新打开保存的登录页（最多 2 次，防止死循环）。
- 跳转到**其他主机**时拦截并提示，绝不把账号暴露给非学校页面。

### 3. 安全约束：仅 https 自动登录

- 保存在设置里的地址如果是 `http://`，程序**不会**在后台自动填写账号密码，会停下来等用户手动操作，并在界面提示原因。
- 原因：明文 http 页面可能被劫持，后台自动提交等于把学号密码交给伪装页面。
- 当前配置为 `https://xha.ouc.edu.cn/a79.htm?...`，不受影响。

### 4. 开机宽限窗口

**`Core.cs` / `AutoPolicy`**

```csharp
public static readonly TimeSpan LogonGrace = TimeSpan.FromSeconds(90);  // 开机宽限
private static readonly TimeSpan GraceInterval = TimeSpan.FromSeconds(5); // 宽限内重试间隔
```

- 开机后 90 秒内，若目标 Wi-Fi 尚未出现，按 5 秒间隔重试，而不是等待 60 秒。
- 宽限结束后恢复正常节奏，避免长期空转。

### 5. 自适应心跳

**`MainForm.cs`**

| 状态 | 心跳间隔 |
|---|---|
| 已连上校园 Wi-Fi | 20 秒（原有节奏） |
| 目标 Wi-Fi 缺失且在开机宽限内 | **1 秒** |
| 宽限结束后仍缺失 | 20 秒 |

- 快速模式下会跳过耗时的全量扫描，避免每秒扫描反而拖慢。
- Wi-Fi 一旦关联成功，1 秒内即开始认证，不再等 20 秒心跳。

### 6. 启动路径优化

| 改动 | 效果 |
|---|---|
| DPAPI 密码延后解密（`LocalStore.LoadProfile()`） | 开机时 DPAPI 服务未就绪会阻塞数秒；现在先返回配置，密码在后台解密，连接不等它 |
| WebView2 在 `Program.Main` 预热 | 浏览器运行时提前创建，与窗口创建、Wi-Fi 关联并行 |
| 固定 WebView2 运行时（`settings.json` → `{"WebView2Channel": "Fixed"}`） | 关闭后台自更新，避免开机时更新程序抢资源 |
| 窗口移到屏幕外而非 `Hide()` | 隐藏窗口会让内嵌浏览器失去视口，认证页无法自动填写；离屏窗口保持正常布局 |
| 计划任务替代 `Run` 注册表项 | 双触发（系统启动 + 用户登录）、最高权限，绕过登录启动队列 |

### 7. 计时日志

**`StartupTiming.cs`**（新增）

- 通过 `--timing` 参数启用，写入 `%LOCALAPPDATA%\CampusConnect\startup-timing.txt`。
- 记录：进程启动、WinForms 初始化、WebView2 就绪、配置读取、密码就绪、网络扫描、Wi-Fi 关联、打开认证页、提交、联网可达。
- 每行带「距 Windows 开机多少秒」，便于判断瓶颈在程序还是系统。
- **只记录阶段名与时间，不记录账号密码或页面内容。**

---

## 四、系统层改动

### 计划任务

| 任务 | 触发 | 作用 |
|---|---|---|
| `CampusConnect\CampusConnect Boot` | 系统启动 + 用户登录 | 以最高权限启动 `CampusConnect.exe --startup --timing` |
| `CampusConnect\CampusConnect Post-Boot Probe` | 用户登录 | 独立探针，记录 `explorer`、程序启动时刻与真实联网时间 |

- 原有的 `HKCU\...\Run\CampusConnect.Windows` 已删除，避免重复启动。
- 单实例互斥保证系统启动与登录两个触发器不会产生两个进程。

### 禁用的启动项（均有备份）

| 项目 | 处理方式 |
|---|---|
| FxSound | 移除启动文件夹快捷方式 |
| Codex++（`CodexPlusPlusWatcher.lnk`） | 移除启动文件夹快捷方式 |
| WPS 更新（`WpsUpdateLogonTask_kangyunfei`） | 禁用计划任务 |
| WPS 唤醒（`WpsWakeWnsLogonTask`） | 禁用计划任务 |
| 夸克更新（`QuarkUpdaterTaskUser1.0.0.21`） | 禁用计划任务 |

保留：网易UU远程（`GameViewer`）——用户需要远程控制功能。

---

## 五、实测效果

| 指标 | 改前 | 改后 |
|---|---|---|
| 开机 → 进入桌面 | 34 秒 | **19 秒** |
| 程序启动 | 桌面后 1 秒 | 与桌面同时（T+19s） |
| 程序启动 → 准备就绪 | 约 14 秒 | **0.4 秒** |
| Wi-Fi 关联 → 判定联网 | 反复重开登录页 | **1.5 秒内** |
| 手动点击 | 每次开机都要点 | **完全不需要** |

### 最终冷启动时间线

```
T+19s   explorer 出现，程序同时启动
T+20s   进程启动
T+25s   配置读取完成（开机阶段磁盘竞争）
T+51s   探针首次 ping 通（Wi-Fi 关联完成）
T+52.5s 程序判定联网
```

### 剩余瓶颈

`T+19s` 到 `T+51s` 这 32 秒是 **Wi-Fi 网卡关联本身**，与程序无关。独立探针在 `T+46s` 时 SSID 仍为空，是直接证据。要进一步压缩只能从网卡驱动、路由器、快速启动等系统层面入手。

---

## 六、测试

| 测试 | 结果 |
|---|---|
| C# 单元测试（`dotnet run --project Windows/tests`） | **2134 项通过** |
| Node 浏览器脚本测试（`node Windows/tests/portal.test.mjs`） | **18 项通过** |
| 实机冷启动验证 | 三轮重启实测，见第五节 |

> 说明：注册表测试段在受限沙箱环境下会跳过（无写 `HKCU\Software` 权限），与代码无关。

---

## 七、回滚方法

| 内容 | 位置 |
|---|---|
| 原始 exe | `tools\backup\CampusConnect-2.0.0-original.exe` |
| 被禁用的启动项 | `tools\backup\startup-items\` |
| 各阶段日志 | `tools\backup\startup-timing-*.txt`、`post-boot-*.txt` |

**还原原始版本：**

```powershell
# 先退出托盘程序
Copy-Item "tools\backup\CampusConnect-2.0.0-original.exe" `
  "C:\Users\kangyunfei\AppData\Local\Programs\CampusConnect\CampusConnect.exe" -Force
```

**恢复 WPS / 夸克计划任务：**

```powershell
Enable-ScheduledTask -TaskName "WpsUpdateLogonTask_kangyunfei" -TaskPath "\"
Enable-ScheduledTask -TaskName "WpsWakeWnsLogonTask" -TaskPath "\"
Enable-ScheduledTask -TaskPath "\QuarkUpdaterUser\QuarkUpdater\" -TaskName "QuarkUpdaterTaskUser1.0.0.21*"
```

**恢复 FxSound / Codex++：** 把 `startup-items\startup-common\FxSound.lnk` 和 `startup-items\startup-user\CodexPlusPlusWatcher.lnk` 复制回对应 Startup 文件夹。

---

## 八、重新编译

项目目标框架为 `net10.0-windows`，需要 .NET 10 SDK。

```powershell
cd C:\Users\kangyunfei\Desktop\CampusConnect-2.0.0\Windows
dotnet publish source\CampusConnect.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=true -o dist\win-x64
```

产物为单文件 `CampusConnect.exe`，替换安装目录下的同名文件即可。

---

## 九、已知限制

1. **Wi-Fi 关联时间不受程序控制**：网卡驱动与路由器决定，实测约 32 秒。
2. **认证只能串行等待**：网页认证必须等 Wi-Fi 关联完成，物理上无法更早。
3. **计时日志会持续追加**：`startup-timing.txt` 每次启动增加一段，长期运行可手动删除，不影响功能。
4. **计划任务需管理员权限创建**：首次安装或迁移时需要用管理员身份注册。
