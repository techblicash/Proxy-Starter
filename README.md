# Proxy Starter（代理启动器）

一个基于 **WPF (.NET 8)** 的 Windows 桌面应用，用来管理/启动 **mihomo（Clash Meta 内核）**，提供订阅管理、节点选择、延迟测试、规则编辑、连接/流量监控、日志查看、托盘菜单等功能。

> 仓库内是 GUI 与管理逻辑源码；**不包含 mihomo.exe**（内核可自行下载/替换，并在设置中配置路径）。

---

## 功能概览

- **内核启动/停止**
  - 启动/停止 mihomo 进程，自动生成/更新配置文件
  - 支持自定义 `CorePath` 与 `ConfigPath`
- **订阅/配置管理（Profiles）**
  - 添加/更新/刷新订阅
  - 支持启用/禁用、自动更新、更新间隔
  - 支持编辑订阅内容/本地文件
- **节点管理（Nodes）**
  - 节点列表展示
  - TCP 测试 / Connect 测试（单个与批量）
  - 选择代理组（Selection Group）并切换
- **规则（Rules）**
  - 规则列表与编辑
  - 支持“Blocked Sites”（自动生成 REJECT / IP-CIDR 规则）
- **连接与流量（Connections）**
  - 连接列表监控
  - 流量统计/监控（Traffic Monitor）
- **日志（Logs）**
  - mihomo 日志展示
- **设置（Settings）**
  - 端口、API Secret、日志级别、Allow LAN、TUN 开关等
  - 外观：主题、字体、Acrylic 透明度
  - 语言：English / 中文
  - 自动启动、自动更新（Velopack）
  - **限速（QoS）**：可选，需要管理员权限（Windows QoS Policy）
- **托盘菜单（Tray）**
  - 常用操作入口（窗口/启动/停止等）

---

## 支持的订阅/节点格式

在订阅解析逻辑中支持：

- **Clash YAML**（含 `proxies` / `proxy-groups`）
- Base64/纯文本链接行订阅，支持协议：
  - `ss://`、`ssr://`、`trojan://`、`vless://`、`vmess://`

---

## 运行环境

- Windows 10/11
- 运行（仅使用）：**.NET 8 Desktop Runtime**
- 开发/编译：**.NET 8 SDK** + Visual Studio 2022（建议）或 `dotnet` CLI

---

## 快速开始（开发者）

### 1. 克隆仓库

```bash
git clone <your-repo-url>
cd "Proxy Starter"
```

### 2. 使用 Visual Studio 打开

双击 `ProxyStarter.sln`，还原 NuGet 包后直接运行（F5）。

或使用命令行：

```bash
dotnet restore
dotnet build
dotnet run --project src/ProxyStarter.App/ProxyStarter.App.csproj
```

------

## 首次使用（建议流程）

1. 打开应用 → **Settings**
2. 设置：
   - **Core Path**：指向你的 `mihomo.exe`（可以是绝对路径，或相对可执行文件目录的相对路径）
   - （可选）**Api Secret**：如果你的 mihomo API 设置了 secret
3. 去 **Profiles** 添加订阅（URL 或 YAML/链接内容）
4. 去 **Nodes** 选择分组与节点，进行测试
5. 回到 Dashboard/托盘菜单 → **Start** 启动内核

------

## 默认端口与 API

默认设置（可在 Settings 修改）：

- Mixed Port：`7890`
- HTTP Port：`7891`
- SOCKS Port：`7892`
- API Port：`9090`
- External Controller：`127.0.0.1:9090`

------

## 数据与配置文件存储位置

应用会把数据写到 Windows 的用户目录：

- 数据目录：`%AppData%\\ProxyStarter`
- 日志目录：`%AppData%\\ProxyStarter\\logs`

常见文件（运行后生成）：

- `nodes.json`：节点信息（UI 展示/测试用）
- `proxies.yaml`：代理定义
- `groups.yaml`：代理组
- `config.yaml`（或你设置的 `ConfigPath`）：启动 mihomo 使用的配置

------

## 项目结构

```
Proxy Starter/
  ProxyStarter.sln
  src/
    ProxyStarter.App/
      Views/        # 页面（Dashboard/Profiles/Nodes/Rules/Connections/Logs/Settings/Tray）
      ViewModels/   # MVVM 逻辑
      Services/     # 进程控制、订阅、配置生成、监控、更新等
      Models/       # 数据模型
      Resources/    # 多语言资源（en-US / zh-CN）
      Assets/       # 图标等资源
```

------

## 注意事项

- 本仓库默认不带 `mihomo.exe`。你需要自行提供，并在 Settings 里配置 **Core Path**。
- “限速（QoS）”功能会调用 Windows QoS Policy，需要**管理员权限**运行才能生效。
- 若你把 `CorePath` 写成相对路径，它会以程序运行目录为基准解析。
