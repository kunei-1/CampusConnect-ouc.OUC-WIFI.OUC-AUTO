# CampusConnect · 开机无感自动连接版

> **本项目是基于 [Ye9137/CampusConnect](https://github.com/Ye9137/CampusConnect) 的修改版。**
> 原作者：**[Ye9137](https://github.com/Ye9137)**，使用 MIT 许可证开源。
> 本仓库在其 2.0.0 版本基础上，针对 **开机自动连接校园网需要手动点击「立即连接」** 的问题做了改进，
> 并针对 **中国海洋大学 OUC-WIFI** 环境做了实测验证。

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Original](https://img.shields.io/badge/Upstream-Ye9137%2FCampusConnect-blue.svg)](https://github.com/Ye9137/CampusConnect)

---

## 这个版本改了什么

原版的功能是：开机自启后在托盘运行，连接校园 Wi-Fi 后**需要用户手动点「立即连接」**才能完成网页认证。

本版把它改成了**真正的开机无感连接**——开机后自动连接 Wi-Fi、自动提交网页认证、自动确认联网，**全程不需要点任何按钮**。

| 指标 | 原版 | 本版 |
|---|---|---|
| 开机 → 进入桌面 | 34 秒 | **19 秒** |
| 程序启动 → 准备就绪 | 约 14 秒 | **0.4 秒** |
| Wi-Fi 关联 → 判定联网 | 反复重开登录页 | **1.5 秒内** |
| 手动点击「立即连接」 | 每次开机都要点 | **完全不需要** |

> 详细的改动说明、技术细节、回滚方法见 **[CHANGELOG-开机无感连接.md](CHANGELOG-开机无感连接.md)**。

---

## 主要改动

### 1. 开机无感连接

- 配置一读完就开始连接，**不再等待内嵌 WebView2 界面加载完成**
- 认证页在后台隐藏容器中完整渲染并自动填写、提交
- 登录启动时若界面加载失败，后台连接继续运行，不弹窗退出

### 2. 认证地址按「同一主机」匹配

学校门户地址带 `wlanuserip=...` 会话参数，重启后参数变化。原版按完整地址匹配会拒绝填写，本版改为按 **协议 + 主机 + 端口** 识别：

```
https://xha.ouc.edu.cn/a79.htm?wlanuserip=10.170.161.235  ← 原地址
https://xha.ouc.edu.cn/eportal/success.jsp?userIndex=1    ← 跳转后，现在也能识别
```

### 3. 安全约束：仅 https 允许自动登录

如果认证地址是明文 `http://`，程序**不会**在后台自动提交账号密码，会停下来等待用户手动操作。
原因：明文页面可能被劫持，自动提交等于把学号密码交给伪装页面。

### 4. 自适应心跳

| 状态 | 心跳间隔 |
|---|---|
| 已连上校园 Wi-Fi | 20 秒 |
| 目标 Wi-Fi 缺失且在开机宽限内 | **1 秒** |
| 宽限结束后仍缺失 | 20 秒 |

Wi-Fi 一旦关联成功，1 秒内即开始认证，不再等 20 秒心跳。

### 5. 启动路径优化

- **DPAPI 密码延后解密**：开机时 DPAPI 服务未就绪会阻塞数秒，现在先返回配置，密码在后台解密
- **WebView2 预热**：浏览器运行时在 `Program.Main` 就提前创建
- **固定 WebView2 运行时**：关闭后台自更新，避免开机抢资源
- **窗口离屏**：用移到屏幕外替代 `Hide()`，保证内嵌浏览器有正常视口
- **计划任务替代 Run 注册表项**：双触发（系统启动 + 用户登录）、最高权限

### 6. 修复重复登录 Bug

原版在认证成功后把状态错误重置，导致每 20 秒心跳都重新打开登录页并重复提交。本版认证成功后页面进入「休息态」。

---

## 下载

请到本仓库的 **[Releases](../../releases)** 页面下载：

| 文件 | 说明 |
|---|---|
| `CampusConnect.exe` | **本版**（单文件，约 117 MB） |
| `CampusConnect-2.0.0-original.exe` | 原版 2.0.0，用于对照与回滚 |

> 仓库本体只包含源码，可执行文件通过 Release 附件分发。

运行环境：Windows 10/11，需要 Microsoft Edge WebView2 运行时（Win11 已内置）。

---

## 安装与设置开机自启

1. 把 `CampusConnect.exe` 放到固定位置（建议 `%LOCALAPPDATA%\Programs\CampusConnect\`）
2. 首次运行，在「校园网设置」中填写 Wi-Fi 名称、认证地址、学号密码，**保存并开启自动连接**
3. 在设置中打开「开机自启」开关
4. 如果希望开机即在登录前启动，可自行注册计划任务：

```powershell
# 以管理员身份运行
$exe = "$env:LOCALAPPDATA\Programs\CampusConnect\CampusConnect.exe"
schtasks /create /tn "CampusConnect\CampusConnect Boot" /sc onstart `
  /tr "`"$exe`" --startup" /rl HIGHEST /f
```

---

## 实测环境

| 项 | 值 |
|---|---|
| 系统 | Windows 11 家庭版 26030 |
| 网卡 | RZ616 Wi-Fi 6E 160MHz |
| 校园网 | 中国海洋大学 `OUC-WIFI`（开放网络） |
| 认证地址 | `https://xha.ouc.edu.cn/a79.htm` |
| 测试结果 | 开机 T+19s 出桌面，T+52.5s 判定联网 |

### 已知瓶颈

`T+19s` 到 `T+51s` 这 32 秒是 **Wi-Fi 网卡关联本身**，与程序无关（独立探针在 `T+46s` 时 SSID 仍为空）。
要进一步压缩只能从网卡驱动、路由器、快速启动等系统层面入手。

---

## 测试

| 测试 | 命令 | 结果 |
|---|---|---|
| C# 单元测试 | `dotnet run --project Windows/tests -c Release` | 2134 项通过 |
| 浏览器脚本测试 | `node Windows/tests/portal.test.mjs` | 18 项通过 |

---

## 原作者与许可证

- **原作者**：[Ye9137](https://github.com/Ye9137)
- **原项目**：[Ye9137/CampusConnect](https://github.com/Ye9137/CampusConnect)
- **许可证**：[MIT](LICENSE) — 版权归 `Ye9137 and CampusConnect contributors`
- **本修改版**：在 MIT 许可下同样开源，版权声明已完整保留

本项目不是原作者官方版本。如果你在使用中遇到问题，请先确认是否为原版已有的行为；
与原版相关的功能请求与 Bug，建议提交到[上游仓库 Issues](https://github.com/Ye9137/CampusConnect/issues)。

原版 README 已保留为 [README-original.md](README-original.md)。

---

## 目录结构

```
├── Windows/                     # Windows 端源码（WinForms + WebView2）
│   ├── source/                  # 主程序
│   ├── tests/                   # 单元测试
│   └── installer/               # NSIS 安装脚本
├── macOS/                       # macOS 端源码（未修改）
├── docs/                        # 截图
├── CHANGELOG-开机无感连接.md     # ★ 本次改动详细说明
├── README-original.md           # 原版 README
├── 2.0-更新说明.md              # 原版 2.0 更新说明
├── VALIDATION.md                # 原版验证文档
└── LICENSE                      # MIT（版权归原作者）
```
