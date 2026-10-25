# AGENTS.md

给 **AI agent / 协作者**看的工程手册：怎么改、怎么验、哪些坑踩过。
面向"用户怎么用/怎么装"请看 [README.md](README.md)，两份文件不要混写。

---

## 0. TL;DR

| 事情 | 命令 |
|---|---|
| 构建 | `dotnet build RemoteCall.sln -c Release` |
| 冒烟测试（必须全绿） | `dotnet run --project tests/RemoteCall.SmokeTests -c Release` |
| 安装到 ClassIsland | `pwsh -File scripts/install.ps1 -StopClassIsland` |
| 安装并顺手把组件放进主界面 | `pwsh -File scripts/install.ps1 -StopClassIsland -AddComponent` |
| 看运行期状态 | `curl http://127.0.0.1:8199/api/state` |

- 宿主：ClassIsland **≥ 2.1.0**（本机 2.1.0.1），目标框架 `net8.0`。
- 默认端口：**8000**（`0.0.0.0`，局域网）/ **8199**（`127.0.0.1`，管理页）；默认密码 **`admin`**。
- 插件 id `remote.call`；组件 GUID `7c4b2e51-9f3a-4d6e-b1c8-2a5d9e7f3b64`；提醒提供方 GUID `e15a8c3d-6b47-4e92-8f1d-3c7b5a2e9d48`。

---

## 1. 仓库结构（谁负责什么）

```
src/RemoteCall/
├─ Core/                     ★ 不引用 ClassIsland / Avalonia，可脱离 GUI 测试
│  ├─ Http/                  自研 TcpListener HTTP/1.1 服务器（解析、keep-alive、ETag、错误码）
│  ├─ Config/                设置模型 / 原子 JSON 读写 / 默认值
│  ├─ Auth/                  PBKDF2 密码哈希、会话存储（只存 token 的 SHA256）
│  ├─ Data/                  消息与设备存储（MessageStore）
│  ├─ Server/                两个服务的路由（LanApi/AdminApi）、状态、生命周期、网卡枚举
│  └─ Web/lan|admin/         内嵌网页（html/css/js，无外部 CDN，作为 EmbeddedResource 打包）
├─ ClassIsland/              宿主集成，全部用 C# 构建 Avalonia 界面（无 XAML）
│  ├─ RemoteMessageComponent(+Settings/SettingsControl)
│  ├─ RemoteCallSettingsPage
│  └─ RemoteMessageNotificationProvider / ServerHostedService / Ui
└─ Plugin/                   插件入口（RemoteCallPlugin）、固定标识（PackageInfo）

tests/RemoteCall.SmokeTests/ 控制台测试：直接 Compile Include Core/**，零第三方依赖
scripts/install.ps1          构建 + 复制到 data\Plugins\remote.call
docs/*.png                   验证截图（README 引用）
```

数据流：网页 → `LanApi` → `MessageStore`（落盘 `store.json`）→
组件订阅 `MessageStore.Changed` 刷新；`RemoteMessageNotificationProvider` 订阅 `MessageAdded` 发提醒。

---

## 2. 硬性约束（改代码前必读）

1. **Core 不得引用 ClassIsland / Avalonia**。冒烟测试只编译 `Core/**`，一旦引入宿主类型，测试工程立刻编译失败。
2. **不写 XAML**。插件侧 Avalonia XAML 编译链在第三方工程里不可靠，所有界面用 C# 构建控件。
3. **除 `ClassIsland.PluginSdk` 外不引第三方 NuGet**；该引用必须带 `ExcludeAssets="runtime; native"`。
4. **绝不把 `ClassIsland.*.dll` / `Avalonia*.dll` 拷进插件目录** —— 宿主用 `AssemblyLoadContext` 加载插件，
   自带副本会导致类型身份冲突（`PluginLoadContext` 找不到就从默认 ALC 回退，这是设计好的）。
5. 所有持久化写入走 `AtomicJsonFile`（写 `.tmp` + `File.Move(overwrite)`），不要直接 `File.WriteAllText` 到正式文件。
6. 用户可见文案用**简体中文**；持久化属性用 **PascalCase**（ClassIsland 用默认 `JsonSerializerOptions` 反序列化组件设置，
   属性名必须与落盘一致）。
7. 设置页/组件设置界面必须沿用宿主规范：`panel.Classes.Add("settings-container")` + `IconText` 分区标题 +
   `SettingsExpander` 行。**不要自己给卡片套背景画刷**（深色主题下 `CardBackgroundFillColorDefaultBrush` 是浅色，会变灰底白字）。
8. `-1 = 不限制` 的语义要四处同步：`PluginSettingsService.Normalize`、`SessionStore.Create`(MaxValue)、
   `MessageStore.TrimLocked`、UI 的 `Minimum`/`maxlength` 与文案。
9. **端口默认值只在 `CoreDefaults` 定义**；UI、JS、文档都引用/插值，不要写死数字。
10. 网络相关逻辑必须与"具体网卡/具体 IP"解耦：地址靠 `LanAddresses` 动态枚举（会过滤虚拟网卡），
    不要在代码里假设任何一台机器的 IP。
11. **设备名的来源分两层**：客户端 `lan/app.js` 的 `detectDeviceName()` 优先（立即显示）；
    服务端 `DeviceNameGuesser.FromUserAgent()` 兜底（没带名字或名字是 `未命名设备` 时按 UA 猜）。
    `未命名设备` 一律当作"还没设置过名字"处理，不要把它当有效名字存下来。

---

## 3. 运行期事实（排错从这里开始）

| 项 | 位置 |
|---|---|
| 插件本体 | `<ClassIsland>\data\Plugins\remote.call\`（`manifest.yml` + `RemoteCall.dll`） |
| 插件数据 | `<ClassIsland>\data\Config\Plugins\remote.call\`：`settings.json`、`store.json`、`sessions.json` |
| 宿主日志 | `<ClassIsland>\data\Logs\*.log`，搜 `远程消息` / `remote.call` |
| 插件内日志 | `LogBuffer`（最近 200 条），通过管理页 `GET /api/state` 的 `logs` 字段读取 |
| 组件布局 | `<ClassIsland>\data\Config\ComponentLayouts\Default.json`（**不要主动改**） |

- 管理页 `GET /api/state` 是排错主力：`status`（监听/端口/错误/地址）、`settings`、`messages`、`devices`、`logs`。
- 改端口/密码后监听会在约 0.3s 内热切换（`ServerHost` 订阅 `PluginSettingsService.Changed`，延迟后重建监听）。
- 改完插件必须**重启 ClassIsland** 才会加载新 DLL；插件 DLL 被占用时安装会失败。

---

## 4. 修改后的标准验证流程

1. `dotnet build RemoteCall.sln -c Release` —— 0 warning / 0 error。
2. 冒烟测试全绿（当前 20 项）。
3. `pwsh -File scripts\install.ps1 -StopClassIsland`（脚本会先正常关闭、失败则结束进程）。
4. 启动 ClassIsland，等 ~22s：
   - `data\Logs\*.log` 出现 `此次会话已加载插件: ...,远程消息(remote.call,...)` 与两条"已开始监听"；
   - `netstat -ano | findstr :8000` / `:8199` 有 LISTENING。
5. `curl http://127.0.0.1:8199/api/state` 检查 `status.lanRunning/adminRunning=true`。
6. **改了 UI 就加一步截图核对**（见第 5 节）。
7. 清理测试消息与临时文件（`POST /api/messages/clear {"scope":"all"}`）。

---

## 5. 无鼠标验证 UI 的手法（都在本机验证过）

- **打开插件设置页**（不用点菜单）：
  ```powershell
  Start-Process ClassIsland.exe -ArgumentList '--uri','classisland://app/settings/remote.call.settings'
  ```
- **抓窗口内容**：`PrintWindow(hwnd, hdc, 2 /*PW_RENDERFULLCONTENT*/)` 能抓到被其它窗口遮住的画面；
  主窗口只有 60px 高且在最上方，屏幕截图容易被浏览器挡住，优先用 PrintWindow。
- **手机页面**：headless Chrome + CDP（`Network.setCookie` 注入会话 +
  `Emulation.setDeviceMetricsOverride` 设手机视口 + `Emulation.setUserAgentOverride` 设手机 UA），
  `Page.captureScreenshot` 出图。
- **组件轮播**：间隔 5s、动画 0.35s；连续抓帧时注意别正好落在切换过程中，否则会误判成"没动 / 只在前两条之间跳"。
- 这些辅助脚本放在 `%TEMP%` 里，**不要提交进仓库**。

---

## 6. 已知坑（全部踩过，改之前先看）

| 坑 | 结论 |
|---|---|
| Avalonia 控件 `Height` 初始为 `NaN` | `Math.Abs(h - x) > 0.5` 对 NaN 恒为 false ⇒ 必须 `double.IsNaN(h) || ...`，否则"设行高"永远不生效（表现为内容偏上、漏出下一条） |
| `StackPanel.Bounds.Height` 在裁剪容器里 = 可视高度 | 要量真实内容高度得显式 `Measure(new Size(width, double.PositiveInfinity))` |
| 主界面其它组件（一言等）刷新会改变行宽 | 触发 `SizeChanged`；此时**不要重置轮播索引**，只做 `RealignToCurrent()` |
| `SettingsExpander.Footer` 是 Auto 列 | 长文本把卡片撑出窗口被裁切 ⇒ 长内容放 `Description`（可换行），Footer 只放开关/数字/短按钮 |
| 手动套卡片背景画刷 | 深色主题下拿到浅色资源 ⇒ 灰底白字；交给 `SettingsExpander` 的模板 |
| 浏览器 Cookie 有效期上限 ~400 天 | 「记住登录 = -1」时服务端 `DateTimeOffset.MaxValue`，Cookie 写 `Max-Age=34560000` 封顶 |
| ClassIsland 是托盘应用 | `CloseMainWindow()` 返回 false，脚本里要用 `Kill()` 兜底（会丢未保存设置，属正常） |
| 插件 DLL 被占用 | 安装前必须先关 ClassIsland，否则 `Remove-Item` 失败 |
| `HttpListener` / ASP.NET Core | 前者非 localhost 前缀需要管理员/urlacl，后者共享框架不在宿主 runtimeconfig ⇒ 只能用 `TcpListener` 自己实现 |
| 控制台编码 | 中文经 bash/curl 传递容易被 GBK 破坏；验证接口时用 Python(UTF-8) 或写文件再 `--data-binary` |
| 浏览器拿不到主机名 | 只能按 UA 识别设备类型/Android 机型；UA 里的 `Android 13`、`wv`、`K` 不是机型，必须过滤（见 `DeviceNameGuesser`） |

---

## 7. 完成标准（提交前自查）

- [ ] `dotnet build -c Release`：0 warning / 0 error
- [ ] 冒烟测试 20/20
- [ ] 插件被宿主加载、两个端口监听、`/api/state` 正常
- [ ] 若改了界面：截图确认（深色主题可读、无横向溢出、与内置设置页观感一致）
- [ ] 若改了接口/设置项：同步更新 `README.md` 的接口表、字段说明、限制说明
- [ ] 清掉测试消息；`data\Config\Plugins\remote.call\` 里没有调试垃圾
- [ ] 仓库里没有 `bin/`、`obj/`、`*.cipx`、临时截图

---

## 8. 不要做的事

- 不要修改 ClassIsland 安装目录里除插件自身（`data\Plugins\remote.call`、`data\Config\Plugins\remote.call`）以外的文件。
- 不要为了"让它开箱可见"去改 `ComponentLayouts/Default.json`（除非用户明确要求；改之前备份）。
- 不要把 IP、端口、绝对路径、用户名写进代码。
- 不要用 `HttpListener`、ASP.NET Core、WebSocket 依赖（当前只有轮询与纯 HTTP）。
- 不要给网页引外部 CDN / 字体 / 统计脚本 —— 局域网设备可能没有外网。
- 不要把 `-1 = 不限制` 只改在 UI 上（三层都要改，否则会偷偷裁剪或偷偷过期）。
