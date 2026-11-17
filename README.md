# WB 版本管理器

> 给 WorkBuddy / WorkBuddy AI 一个「留在这一版」的开关：检测当前版本、拦截自动更新、随时回退到任意历史版本，并顺手把安装包链接整理好。

[![Platform](https://img.shields.io/badge/platform-Windows-0078D4?logo=windows)](#快速开始)
[![Client](https://img.shields.io/badge/client-C%23%20%C2%B7%20.NET%20Framework%204.8-512BD4)](#c-客户端桌面版)
[![Node](https://img.shields.io/badge/Node.js-20%2B-339933?logo=node.js)](#网页版)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![No dependencies](https://img.shields.io/badge/dependencies-0-brightgreen)](#快速开始)
[![Release](https://img.shields.io/github/v/release/firlsy/wb-version-manager)](https://github.com/firlsy/wb-version-manager/releases/latest)

WorkBuddy 桌面端会在后台把完整安装包下载到用户目录，下次启动时静默安装，界面里没有「停在当前版本」的选项。这个工具补上这个开关：**锁定版本 + 三层拦截更新 + 一键回退**，并且不动安装目录里的任何文件。

两种前端，功能对齐：

- **C# 桌面客户端**（v1.1.0 新增）：单文件 exe，免安装、免运行时，双击即用；
- **网页版**：Node.js 起一个本地服务，浏览器里操作，适合想直接看源码或用脚本调用的场景。

## 下载

到 [Releases](https://github.com/firlsy/wb-version-manager/releases/latest) 下载（以页面上的最新版本号为准）：

| 文件 | 说明 |
| --- | --- |
| `WB-Version-Manager-universal-v1.3.2.exe` | **C# 桌面客户端 · 全架构单文件**，260 KB，双击即用，不需要装任何运行时。AnyCPU（`ILONLY`）构建：x86 系统以 32 位运行、x64 系统以 64 位运行、Windows on ARM 上 JIT 成原生 ARM64 —— 一个文件覆盖三种架构 |
| `wb-version-manager-v1.3.2.zip` | 网页版便携包（含 C# 客户端源码与构建脚本），解压后双击 `启动工具.bat`（需要 Node.js 20+） |
| `SHA256SUMS.txt` | 上面两个文件的校验值 |

> 文件名里的版本号跟界面标题、`--version` 输出永远一致：构建脚本从 `client/src/Core.cs` 的 `AppInfo.Version` 读出来再拼文件名。

![主界面](docs/screenshot-main.png)


## 功能

- **自动识别两款客户端**：WorkBuddy（国内版）与 WorkBuddy AI（国际版），读取 `install-manifest.json` 得到真实产品版本，而不是根目录的 Electron 版本号。
- **默认锁定当前版本**：一键把正在使用的版本钉住，同时关掉客户端的启动强制更新。
- **拦截自动更新**：关启动开关 → 回收已下载的安装包 → 对缓存目录加写入拒绝，三层同时生效。安装目录零改动，不需要管理员权限，也不会再弹「更新需要管理员权限」。
- **版本清单**：内置官方历史版本目录（当前 102 个版本），标记「当前安装 / 已缓存」，支持排序与快速定位。
- **两版安装包彻底分开**：国内版只取 `download.codebuddy.cn` / 腾讯云 COS，国际版只取 `download.codebuddy.ai`。两套 URL 分开存（`packages` / `intlPackages`），国际版取不到官方包时显示「—」，绝不拿国内版顶替。旧版本工具下错的「错版包」会被识别出来并标记，不能用于回退。
- **官方更新说明**：选中任意版本即显示官网更新日志原文要点，附官方原文链接。
- **下载指定版本**：直接从官方 CDN 下载 Windows 安装包，带进度、速度与断点文件清理。
- **导出 / 复制链接**：按国内版 / 国际版分段列出，取不到的明确写「官方未提供该版本」；也可导出全部版本的 TXT 清单。
- **清理无用缓存**：缓存卡片按客户端分别显示、分别清理，只删掉既不是当前版本也不是锁定版本的安装包；另一个客户端完全不受影响。
- **刷新目录同时重扫安装**：点「刷新目录」会把版本清单、本机安装检测、拦截状态一起重扫，装完客户端回来点一下就能看到真实版本。
- **回退步骤内置**：左下角常驻六步速查，点「完整说明」有详细版——数据在哪、装失败了怎么办。
- **回退只准备环境，安装由你双击**：工具结束进程、自动解除锁定、清熔断记录、回收待装包，然后在资源管理器里选中安装包；安装器自己匹配原安装目录，工具不指定路径、不静默安装。
- **版本号与自检更新**：客户端窗口标题带版本号（`WB 版本管理器 v1.3.2`），标题旁的「检查更新」直接查 GitHub Releases，有新版本会给出下载页；网页版在品牌标题后面显示同一版本号，右上角也有「检查更新」，启动时会静默查一次，有新版本就把按钮点亮。两边的版本号都从 `AppInfo.Version` 读，跟 exe 文件名永远一致。点左上角 logo 或标题可跳到仓库。
- **版本漂移提示**：锁定后若被其他方式升级，顶部会提示「锁定 x，当前 y」，可一键回退。
- **可选后台守护**：注册计划任务，每 30 分钟检查一次版本漂移。

![版本详情](docs/screenshot-detail.png)

## 快速开始

### C# 客户端（桌面版）

要求：Windows 10/11。**不需要**安装 .NET 运行时或 Node.js——用的是系统自带的 .NET Framework 4.8。

```text
双击  WB-Version-Manager-universal-v1.3.2.exe
```

一个文件就够，不用管本机是 x86、x64 还是 ARM64 —— 运行时会自己挑。

界面分三块：左侧两张客户端卡片（版本 / 锁定状态 / 一键锁定）、中间版本清单、下方官方更新说明与运行日志；左下角是回退步骤速查，点「完整说明」看详细版。

![C# 客户端](docs/screenshot-client.png)

也支持命令行，方便脚本化：

```powershell
.\WB-Version-Manager-universal-v1.3.2.exe --status                # 查看两款客户端的版本与拦截状态
.\WB-Version-Manager-universal-v1.3.2.exe --lock workbuddy        # 锁定（workbuddy | workbuddyai）
.\WB-Version-Manager-universal-v1.3.2.exe --unlock workbuddy
.\WB-Version-Manager-universal-v1.3.2.exe --clear-cache workbuddy # 只清国内版的无用安装包缓存
.\WB-Version-Manager-universal-v1.3.2.exe --links workbuddyai     # 列出国际版各版本的安装包链接
.\WB-Version-Manager-universal-v1.3.2.exe --refresh               # 联网刷新版本目录（含国际版通道）
.\WB-Version-Manager-universal-v1.3.2.exe --version               # 显示工具版本
```

### 网页版

要求：Windows 10/11、[Node.js](https://nodejs.org/) 20 或更高版本。无第三方依赖，不需要 `npm install`。

```text
双击  启动工具.bat
```

或手动启动：

```powershell
node server.js
```

浏览器会自动打开 `http://127.0.0.1:17321`（端口被占用时自动顺延）。关闭命令行窗口即停止工具。


## 使用

1. **确认版本**：顶部两张卡片显示两款客户端各自的安装目录与当前版本。
2. **锁定**：点「锁定当前版本」。工具会记录版本、关掉启动强制更新、把已下载的更新包回收进 `store/`，并对缓存目录加写入拒绝。
3. **查看更新内容**：在版本清单点任意版本，右侧抽屉显示官方更新日志；「查看官方更新日志原文」可跳转官网。
4. **下载 / 导出**：行内「下载」获取 Windows 安装包；勾选多个版本后可批量复制链接或导出表格。
5. **回退**：选中目标版本 →「回退安装」。工具先准备环境（结束进程、自动解除锁定、清熔断记录、回收待装包），再把安装包在资源管理器里选中；双击它，安装器自己匹配原安装目录，装完回工具点「锁定当前版本」。

## 工作原理

### 锁定：三层同时生效，安装目录一个字节都不改

从 5.9 起，客户端的自动更新流程是这样的：

1. 启动时读 `app-config.json` 里的 `startupForceAutoUpdate`，为真就执行「早期静默更新」；
2. 定期请求 `https://www.workbuddy.cn/v2/update?platform=workbuddy-win32-x64-user&version=<当前版本>`；
3. 把新版本下载到 `%LOCALAPPDATA%\Temp\workbuddy-update-x64-workbuddy\WorkBuddy-Setup-<版本>.exe`；
4. 下次启动时尝试静默安装，安装目录不可写就弹「更新需要管理员权限」。

只堵第 4 步不够——弹窗会一直出现。所以锁定做三件事：

| 层 | 动作 | 落点 |
| --- | --- | --- |
| 启动开关 | 写入 `startupForceAutoUpdate: false` | `%USERPROFILE%\.workbuddy\app\app-config.json`<br>`%USERPROFILE%\.workbuddy\settings.json` |
| 缓存回收 | 已下载的安装包移入本地仓库，可继续用于回退 | `store/<应用>/WorkBuddy-win32-x64-user-<版本>-recycled.exe` |
| 写入阻断 | 对缓存目录加 `DENY (WD,AD,WEA,WA)` ACL，下载落盘直接 `EPERM`，但保留目录枚举 | `%LOCALAPPDATA%\Temp\workbuddy-update-x64-workbuddy` |

同时清掉 `win-install-failure.json` 里的失败计数——客户端连续失败 3 次或 24 小时内失败过，就会进入「熔断」，每次启动都弹一次管理员权限提示。清掉计数才能真正止住弹窗。

旧版（5.9 之前）的更新器缓存 `%LOCALAPPDATA%\@genieworkbuddy-desktop-updater\installer.exe` 也一并处理：文件移入隔离区，原位置换成同名目录，写入必然失败。

解除锁定时反向操作：删掉 ACL 拒绝项、把 `startupForceAutoUpdate` 恢复成原来的值，已回收的安装包留在 `store/` 里不删。

还原规则有个细节：只有当锁定前这个键**明确是 `true`**（用户自己开过自动更新）才写回 `true`；否则直接删键，让客户端回到自己的默认行为。这样无论上一次是哪个前端锁的，解除之后都真的会恢复更新。

> 两款客户端的缓存目录、配置目录彼此独立，拦截按应用分别记录；旧版更新器目录是共用的，只要还有一个应用处于锁定状态就保持拦截。

### 回退：工具准备环境，安装交给你

从高版本降到低版本，真正会出问题的不是安装包本身，而是装之前那一堆残留状态。所以点「回退安装」时，工具按顺序做五件事：

| 顺序 | 动作 | 为什么 |
| --- | --- | --- |
| 1 | 校验安装包 SHA-256（官方提供时） | 下坏了就别装 |
| 2 | 结束正在运行的客户端 | 否则安装器写不进程序目录 |
| 3 | **自动解除锁定**：删 ACL 拒绝项、还原 `startupForceAutoUpdate`、清拦截记录 | 缓存目录还只读的话，安装器会直接失败 |
| 4 | 备份并清掉 `win-install-failure.json` / `win-install-attempt.json` | 失败计数会让客户端进入熔断，拒绝再装低版本 |
| 5 | 回收客户端更新缓存里待装的包，然后在资源管理器里选中安装包 | 不回收的话，装完旧版立刻又被顶回新版 |

做完这五步，**工具就退出安装流程**：不传 `/D=`、不加 `/S`、不提权，由你双击安装包，让官方安装器自己识别原安装目录。这样既不会把国际版装进国内版目录，也不会因为工具猜错路径而写坏环境。

装完回到工具点「刷新目录」（会同时重扫本机安装），再点「锁定当前版本」。回退对话框里点「是」的话，这两步工具会替你做。

安装器只替换程序文件。以下数据目录不会被触碰：

| 数据 | 路径 |
| --- | --- |
| 国内版配置 | `%USERPROFILE%\.workbuddy` |
| 国际版配置 | `%USERPROFILE%\.workbuddy-ai` |
| 更新缓存 | `%LOCALAPPDATA%\Temp\workbuddy-update-*` |
| 旧版更新器 | `%LOCALAPPDATA%\@genieworkbuddy-desktop-updater` |

> 网页版（Node.js）仍保留旧的静默安装方式：`installer.exe /S --updated /UPDATE=1 /D=<安装目录>`。桌面客户端从 v1.3.0 起改为上面的「准备 + 手动双击」。

## 数据来源

| 内容 | 来源 |
| --- | --- |
| 国内版最新版本与安装包 | `https://copilot.tencent.com/v2/update?platform=workbuddy-win32-x64-user&version=0.0.0` |
| 国际版最新版本与安装包 | `https://www.workbuddy.ai/v2/update?platform=workbuddy-win32-x64-user&version=0.0.0` |
| 国内版历史版本与下载链接 | [官方历史版本页](https://www.workbuddy.cn/docs/workbuddy/Download-History) |
| 更新说明 | [官方更新日志](https://www.workbuddy.cn/docs/workbuddy/Changelog) |

**两个通道返回的是两套完全不同的安装包**：国内版在 `download.codebuddy.cn` / 腾讯云 COS，国际版在 `download.codebuddy.ai`。文件名里的 `-<hash8>` 后缀是必需的，去掉就 404。

国际版有个硬限制：官方**只发布当前版本**的安装包，没有历史版本页（`/docs/workbuddy/Download-History` 是空壳页，sitemap 里也没有）。所以工具的做法是——每次刷新时把国际版当前版本的地址记进 `intlHistory`，日积月累形成可用清单；取不到的版本在界面上显示「—」，不会拿国内版的链接顶替。国际版回退主要靠本机缓存：客户端自己更新时下载的包会被回收进 `store/workbuddyai/`，那些都是真正的国际版安装包。

「同步目录」会增量同步：只补抓缺失的版本说明，已有缓存不会被覆盖。目录保存在 `data/catalog.json`，断网也能查看已同步内容。它和「检查更新」是两件事——前者同步**被管理软件的版本目录**，后者查**这个工具自己**有没有新版。

## 三种入口

| 入口 | 依赖 | 适合 |
| --- | --- | --- |
| `WB-Version-Manager-universal-v1.3.2.exe` | 无（.NET Framework 4.8 系统自带） | 日常使用，图形界面 + 命令行 |
| `启动工具.bat` / `node server.js` | Node.js 20+ | 想直接看前端实现、改样式、远程调试。页头同样显示版本号，右上角有「检查更新」 |
| `wb-vers-manager.ps1`（仓库内，不随 Release 发布） | 无（Windows PowerShell 5.1） | 克隆仓库后只想在终端里做锁定/解除 |

```powershell
.\wb-vers-manager.ps1 status   # 查看版本、待安装更新、拦截状态
.\wb-vers-manager.ps1 lock     # 关闭启动更新、回收安装包、拒绝缓存写入
.\wb-vers-manager.ps1 unlock   # 解除拦截并还原配置
.\wb-vers-manager.ps1 list     # 列出隔离区内容
```

三个入口操作的是**同一份客户端配置**，可以混用。各自的 `state.json` 只记录自己的操作历史；判断「当前是否已锁定」时以实际配置和 ACL 为准，所以换一个前端打开也不会显示错。

## 目录结构

```text
.
├─ client/                    # C# 桌面客户端
│  ├─ build.bat               # 一键构建（调用系统自带 csc，无需 SDK）
│  └─ src/
│     ├─ Program.cs           # 入口 + 命令行模式
│     ├─ MainForm.cs          # 主界面
│     ├─ Theme.cs             # 配色与自绘控件（圆角卡片/按钮/胶囊/进度条）
│     ├─ NotesView.cs         # 自绘更新说明视图（自动换行 + 滚动）
│     ├─ Json.cs              # 极简 JSON 读写
│     ├─ Core.cs              # 应用画像、路径、格式化
│     ├─ StateStore.cs        # 锁定记录与设置
│     ├─ Detector.cs          # 安装目录与版本识别（含 asar 头部解析）
│     ├─ Catalog.cs           # 官方目录解析与联网刷新
│     ├─ Block.cs             # 三层拦截（配置 / 回收 / ACL）
│     ├─ Download.cs          # 下载与 SHA-256
│     └─ Install.cs           # 回退环境准备（不代替用户安装）
├─ server.js                  # 网页版本地 HTTP 服务（仅监听 127.0.0.1）
├─ guard.js                   # 版本守护：检查/恢复漂移
├─ lib/
│  ├─ detect.js               # 安装目录与版本识别
│  ├─ catalog.js              # 官方目录增量同步
│  ├─ downloads.js            # 下载管理（进度/取消）
│  ├─ install.js              # 官方静默安装与校验
│  ├─ block.js                # 三层拦截实现
│  ├─ lock.js                 # 锁定状态与计划任务
│  ├─ parse.js                # 官网页面解析
│  └─ store.js                # 状态读写
├─ public/                    # 前端（原生 HTML/CSS/JS，无构建）
├─ scripts/
│  ├─ build-catalog.mjs       # 重建官方目录快照
│  └─ dev-screenshot.mjs      # 截图（自动脱敏）
├─ data/catalog.json          # 官方版本目录快照（公开数据）
├─ docs/                      # 截图与说明
└─ 启动工具.bat
```

### 自己构建 C# 客户端

```powershell
cd client
.\build.bat
```

产物只有一个：`dist\WB-Version-Manager-universal-v<版本号>.exe`。文件名里的版本号不用手改——`build.bat` 从 `client\src\Core.cs` 的 `AppInfo.Version` 读出来拼进文件名，所以文件名、窗口标题、`--version` 输出三处永远一致。构建脚本调用 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，并把 `data/catalog.json` 作为资源嵌进去，所以 exe 拷到任何地方都能离线看版本清单。

> 为什么一个文件能覆盖三种架构：编译参数是 `/platform:anycpu`，产物 PE 头里 `machine = 0x014c`、`PE32`，但 COR 头的 `Flags = 0x00000001`，即**只有 `ILONLY`、没有任何 32BITREQUIRED / 架构绑定**。CLR 加载时按宿主进程位数 JIT：32 位系统跑 32 位，x64 跑 64 位，Windows on ARM 直接 JIT 成原生 ARM64。所以不需要再分 `-arm64.exe` / `-x86.exe` 两个包。
>
> 顺带一个限制：.NET Framework 4.8 自带的旧版 `csc.exe` 不认 `/platform:arm64`（报 CS1672），`arm` 也只是 32 位 ARM，所以 `anycpu` 是唯一正确解法，而不是退而求其次。


## 常见问题

**锁定后客户端还弹「更新需要管理员权限 / 自动更新未能完成」？**

那是 `win-install-failure.json` 的失败熔断：客户端失败过一次后，24 小时内每次启动都会重弹。v1.0.1 起锁定会同时清掉这条记录、关掉启动强制更新、拒绝缓存写入，弹窗不会再出现。旧版本请先解除锁定再重新锁定一次，或手动删除 `%USERPROFILE%\.workbuddy\app\win-install-failure.json`。

**锁定后客户端提示更新失败？**
这是预期行为：更新器无法写入安装包。需要更新时先在工具里解除拦截，或直接安装目标版本。

**会丢工作区或任务吗？**
不会。安装器只写程序目录；工作区、任务、技能与资料库都在用户目录，工具也会在安装前展示这些路径。

**需要管理员权限吗？**
锁定/解除不需要——配置写入和 ACL 拒绝都在当前用户权限内完成。回退时工具本身也不提权，UAC 是安装器自己要的（因为要写 `Program Files`）。

**C# 客户端把数据存在哪？**
`%LOCALAPPDATA%\wb-version-manager\`：`state.json`（锁定记录）、`store\`（下载与回收的安装包）、`catalog.json`（联网刷新后的版本目录）。想换位置可以设环境变量 `WBVM_DATA_DIR`。网页版把同类数据放在项目目录的 `data\` 与 `store\` 下——两边互不覆盖，锁定状态以客户端实际配置为准。

**WorkBuddy 和 WorkBuddy AI 的版本为什么可能不同？**
两者是**两套独立的发布通道**：国内版走 `copilot.tencent.com`，国际版走 `www.workbuddy.ai`，安装包也分别放在 `download.codebuddy.cn` 和 `download.codebuddy.ai`。版本号、发布节奏都不一样（截稿时国内版 5.11.3、国际版 5.9.4），安装包不能混用。工具分别检测、分别锁定、分别下载。

**为什么不用根目录的 `version` 文件判断版本？**
那是 Electron 的版本（如 `37.10.3-43`），不是产品版本。产品版本在 `resources/install-manifest.json` 的 `appVersion` 字段。

## 数据与隐私

仓库内只包含公开的官方版本目录，不含任何本机用户名、安装路径或使用记录。运行时产生的 `data/state.json`、`store/`、`exports/` 已加入 `.gitignore`；截图脚本在保存前会把 `C:\Users\<name>` 之类的路径替换为 `<user>`。

## 更新记录

**v1.3.2**
- **三架构合并成一个文件**：不再分 `-arm64.exe` 和 `-x86.exe`，只发布一个 `WB-Version-Manager-universal-v1.3.2.exe`。`/platform:anycpu` + `ILONLY` 构建，x86 系统按 32 位跑、x64 按 64 位跑、Windows on ARM 由 CLR JIT 成原生 ARM64，三种架构通吃，下载时不用再判断自己是什么机器。
- **产物命名规则固定下来**：`WB-Version-Manager-universal-v<版本号>.exe`，版本号是真实版本，不是占位符。`build.bat` 直接从 `client\src\Core.cs` 的 `AppInfo.Version` 读版本号拼文件名，不再手工维护第二处版本字符串——改一处，exe 文件名、窗口标题、`--version` 输出同时生效。
- 构建脚本精简：一次只编一个产物，删掉了原来 `/platform` 循环编译两遍的逻辑。
- **网页版补上版本号与检查更新**：品牌标题后面显示 `v1.3.2`（跟 exe 文件名、窗口标题同一来源），右上角新增「检查更新」，查 GitHub Releases 比对版本；启动时静默查一次，有新版本直接把按钮点亮成「有新版本 vX」，再点一下打开下载页。原来那个按钮改名为「同步目录」——它同步的是被管理软件的版本目录，跟工具自更新是两回事。
- 网页版新增 `/api/app`（版本、仓库地址、exe 文件名）与 `/api/update-check`（服务端代查 GitHub，避开浏览器跨域与限流）。
- **exe 换上自己的图标**：`client/app.ico` 内含 16/20/24/32/40/48/64/128/256 九个尺寸，跟界面左上角的品牌标同一套渐变（`#2f6bff → #6a5bff → #9a4dff` + 白色 WB）。九个尺寸都是完整的「WB」——小尺寸按像素单独调过字号（16px 用 38 号、256px 用 25 号），缩到任务栏 16px 也不会糊成一个字母。`build.bat` 用 `/win32icon` 编进去，不再是 .NET 默认那张白纸图标。

**v1.3.1**
- 窗口标题带版本号：`WB 版本管理器 v1.3.1`。
- 标题右侧新增「检查更新」：走 GitHub Releases API 比对版本，有新版本直接给出下载页；网络不通时提示可手动打开。
- 点左上角 logo、「版本管理器」标题或副标题，直接打开 GitHub 仓库。
- 命令行新增 `--version`。

**v1.3.0**
- **修复致命问题：国际版的下载链接全部指向了国内版**。根因是历史版本目录只从国内版文档站抓取，一个版本只有一套 `packages`，两个客户端共用。现在按客户端分开存：国内版走 `packages`（`download.codebuddy.cn` / 腾讯云 COS），国际版走 `intlPackages`（`download.codebuddy.ai`），刷新时分别请求两个更新通道。取不到官方包就显示「—」，绝不跨版顶替。
- 识别并标记**错版包**：旧版本工具把国内版安装包下进了 `store/workbuddyai/`，光看文件名分不出来，现在用官方链接的文件名反查归属，命中另一版就标「错版」，不能用于回退，并计入「清除无用缓存」。
- **回退安装不再静默运行**：去掉 `/S` 与 `Verb=runas`，工具只把环境准备好，然后在资源管理器里选中安装包，由你双击安装，安装器自己匹配原安装目录。
- 回退环境准备补齐五步：校验 SHA-256 → 结束客户端 → **自动解除锁定**（删 ACL 拒绝、还原启动开关）→ 备份并清掉 `win-install-failure.json` / `win-install-attempt.json` 熔断记录 → 回收待装包。从高版本降回低版本不会再因为熔断或只读目录报错。
- 点「回退安装」时自动解除锁定；装完在对话框点「是」，工具会重新检测版本并重新锁定。
- **「刷新目录」现在同时重扫本机安装**：以前只刷版本清单，装完客户端回来点它仍然显示「未检测到安装」。现在会重新跑一遍安装检测、拦截状态、缓存统计，并在日志里逐条列出两款客户端的检测结果。
- 联网请求支持 `HTTPS_PROXY` / `HTTP_PROXY` 环境变量，并显式开启 TLS 1.2——代理环境下不会再报「发送请求时错误 → 未能创建 SSL/TLS 安全通道」。
- 新增命令行 `--links [appId]`（列出某版各版本安装包链接）与 `--refresh`（联网刷新目录）。

**v1.2.0**
- 左下角新增「从新版本回退到旧版本」步骤卡片：六步走完，旁边一个「完整说明」按钮打开详细版（含数据是否受影响、SHA-256 校验、失败怎么处理）。
- 缓存卡片新增「清除无用缓存」：只删既不是**当前安装版本**也不是**锁定版本**的安装包，连同未完成的 `.part` 碎片；点之前会列出将删除的文件与可释放空间。
- 缓存按客户端分开：卡片标题显示当前选中的是哪一版，按钮只清这一版的目录，另一个客户端一个字节都不动。命令行对应 `--clear-cache workbuddy | workbuddyai`。
- 「打开缓存目录」现在直接打开当前客户端缓存包所在目录；和网页版放在同一目录时（发行包就是这种结构），两边的缓存会一起统计。
- 客户端按架构出两个构建：`-arm64.exe`（AnyCPU，Windows on ARM 原生运行）与 `-x86.exe`（32 位）。`build.bat` 一次编出两个。
- Release 不再提供 `.ps1` 脚本；便携包里也去掉了，命令行能力已经由 exe 的 `--status / --lock / --unlock / --clear-cache` 覆盖。

**v1.1.1**
- 修复切换客户端后顶部徽章不刷新：`Badge` / `FlatButton` 是自绘控件，`Text` 变化时不会自动重绘，导致选中 WorkBuddy AI 后仍显示国内版的「已锁定 5.9.3 / 更新缓存 空 / 未运行」。现在文字一变就重绘。
- 徽章宽度改为跟随文字，长文案（如 `更新缓存 3 个 · 1.2 GB`）不再被裁掉，右侧路径标签自动让位。
- 「运行中 / 未运行」改为每 5 秒自动刷新，开了或关了客户端不用手动点「刷新目录」。

**v1.1.0**
- 新增 C# 桌面客户端：单文件 exe，免安装、免运行时，界面与网页版功能对齐，另带 `--status / --lock / --unlock` 命令行。
- 缓存目录的写入阻断从 `DENY (W)` 收窄为 `DENY (WD,AD,WEA,WA)`：依然挡住下载落盘，但保留目录枚举，工具能正常统计缓存内容。
- 解除锁定的还原策略修正：只有锁定前该键明确是 `true` 才写回，否则删键回默认。跨前端锁定/解除不再出现「解了还在锁」。
- 客户端与网页版混用时，锁定状态以实际配置与 ACL 为准，不再依赖各自的 `state.json`。

**v1.0.1**
- 修复锁定后仍弹「更新需要管理员权限」：清掉 `win-install-failure.json` 熔断记录，并关闭启动强制更新、回收缓存安装包、拒绝缓存写入。

## 免责声明

非官方社区工具，与 WorkBuddy / 腾讯无隶属关系。请仅在你有权使用的软件上使用。锁定与回退会改变本机客户端版本，操作前请确认当前工作已保存。

## License

[MIT](LICENSE)
