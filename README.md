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
