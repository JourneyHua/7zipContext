# 7zipContext

给 7-Zip 补一套干净的 Windows 11 右键菜单。

**这不是压缩软件，而是 7-Zip 的 shell 增强层。** 压缩、解压、加密、分卷、进度条、密码框
全部由 7-Zip 本体（`7z.exe` / `7zG.exe` / `7zFM.exe`）完成；7zipContext 只做一件事：
把右键菜单做干净 —— Win11 紧凑菜单用 AppX `IExplorerCommand`，经典菜单用 `IContextMenu` 内联目录树。

> **仓库不携带 7-Zip 本体。** 首次安装时会自动探测本机已有的 7-Zip；没有则从官方发布页
> 下载、校验并解包到用户目录。详见下方「7-Zip 引擎」。

## 功能

| 入口 | 功能 |
|------|------|
| 右键 · 智能解压到此处(S) | 单根文件夹 → 解出文件夹本身；多条目 → 建压缩包同名文件夹 |
| 右键 · 智能解压到此处并删除 | 同上，**完全成功后**把压缩包删进回收站（失败绝不删） |
| 右键 · 解压到 xxx\(T) | 解压到以压缩包名命名的文件夹 |
| 右键 · 解压到其他文件夹(B)... | 选目录解压 |
| 右键 · 添加到 \<名\>.zip / .7z | 一步压缩（多选时以父文件夹名命名） |
| 右键 · 快速压缩到 \<名\>.zip | 压缩并打开所在文件夹 |
| **经典菜单 · 压缩文件预览** | 菜单里直接铺开压缩包结构，不开窗、不解压 |

预览树的默认规则：根目录的文件夹和文件完整列出、文件夹在前；每个根文件夹只展开前 3 个
子文件夹，子项紧跟自己的父目录；子层不列文件。条数上限与展开层数都能在
`dist\7zipContext.ini` 里调整。`.zip` 用 .NET 直读中央目录，不依赖引擎。

两套菜单各管各的：

- **Win11 现代菜单**（右键第一层）：7 个动词，没有预览
- **经典菜单**（右键 → 显示更多选项 / `Shift+F10`）：上面那 7 个 + 内联预览树

进度条和密码框全部由 `7zG.exe` 提供（7-Zip 自己的窗口）。7zipContext 唯一的界面是设置面板，
所有压缩/解压交互都不弹自己的窗口。

## 安装

### 普通用户：图形化安装包

1. 下载并**完整解压** [7zipContext-Setup.zip](https://github.com/JourneyHua/7zipContext/releases/latest)，不要直接在压缩软件窗口里运行。
2. 双击 `7zipContext.Setup.exe`，点击 **「安装 / 修复」**。
3. 等待安装与自检完成，再右键文件或文件夹即可使用。

程序安装到 `%LOCALAPPDATA%\7zipContext\app\`，不需要管理员权限；安装完成后可以删除
下载的 ZIP 和解压目录。已安装目录里也保留了一份图形化安装卸载工具。
若本机没有完整的 7-Zip，安装器会自动从官方发布页下载、校验并解包。
安装期间资源管理器会短暂重启，这是正常现象。

需要 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（自检会提示）。

### 高级用户：从源码安装

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -RestartExplorer
```

脚本依次完成：发布 → **准备 7-Zip 引擎** → 注册两套菜单 → 清理旧版本残留 → **自检 10 项**。
生成图形化安装包则运行 `scripts\package.ps1`，产物为 `release\7zipContext-Setup.zip`。

可选参数：

| 参数 | 用途 |
|------|------|
| `-SkipPublish` | 跳过编译发布（只改注册逻辑时重跑更快） |
| `-RestartExplorer` | 完成后重启资源管理器，菜单立即可见 |
| `-EnginePath <目录>` | 离线安装：指向本机已有的 7-Zip 目录（需含四件套） |
| `-EngineSource <URL>` | GitHub 不可达时，指定可达的官方安装包 URL |
| `-SkipEngineCheck` | 跳过「更新检查」提示（不影响引擎准备） |

## 设置面板

AppX 包在开始菜单注册了一个 **7zipContext** 图标，点它（或直接双击
`dist\7zipContext.exe`、运行 `dist\7zipContext.exe --settings`）会打开设置面板，四个页签：

| 页签 | 内容 |
|------|------|
| **预览规则** | 可视化编辑根目录条数、每层展开数、是否显示子层文件、展开层数；「保存」后下一次右键生效，可「恢复默认」或直接打开 ini |
| **7-Zip 引擎** | 查看版本/来源/目录/SHA-256；一键「检查更新」「升级引擎」「重新检测」，或打开引擎目录 |
| **默认打开方式** | 查看 `.zip/.7z/.rar` 的注册状态；重新注册，或打开系统「默认应用」页手动选一次 |
| **关于** | 版本、许可与商标声明、项目主页、卸载说明 |

设置窗口**只由无参启动触发**；右键菜单的所有动词以及 `--ensure-engine` 等引擎命令仍以隐藏窗口
方式运行，绝不弹窗（否则会卡住 `install.ps1`）。预览规则仍可直接改 `dist\7zipContext.ini`，两种
方式等价。

## 7-Zip 引擎

仓库和 `dist\` 都不带 7-Zip 二进制。安装阶段只做一次「确保引擎可用」：

1. **探测本机**：自身缓存 → `PATH`（含 scoop shim 解引用）→ 注册表 → 常见安装路径。
   要求 `7z.exe / 7zG.exe / 7zFM.exe / 7z.dll` 四件套齐全，缺 7zG 会丢进度窗和密码框。
   找到就直接复用，**完全不联网**。
2. **联网下载**：从 GitHub Release API 取最新版本号与官方 x64 安装包的 `digest`（SHA-256），
   下载后比对，再用 Windows 自带 `tar.exe` 直接解开安装器（免安装、免管理员、不写 Program Files）。
3. **落地**：`%LOCALAPPDATA%\7zipContext\engine\{版本}\`，同目录保留官方 `License.txt`；
   写 `engine.path`（当前生效目录）与 `engine.json`（版本、来源、SHA-256、安装时间）。

**菜单运行时只读 `engine.path`**：零网络、零子进程、微秒级。经典菜单 handler 跑在
explorer.exe 进程内且有 250ms 硬超时，任何联网动作都会卡死右键菜单 —— 所以下载永远只在安装阶段发生。

常用命令：

```powershell
dist\7zipContext.exe --engine-info --show            # 查看当前引擎
dist\7zipContext.exe --check-engine-update --show   # 只检查、只提示（每天最多联网一次）
dist\7zipContext.exe --update-engine --show         # 显式升级；旧版本目录保留可回滚
```

引擎缺失时菜单不会静默失效：`.zip` 预览树仍可用，其它格式显示「未找到 7-Zip 引擎，请运行 install.ps1」。

## 卸载

打开开始菜单里的 **7zipContext → 关于 → 打开卸载工具**，或运行安装目录里的
`7zipContext.Setup.exe`，点击 **「卸载」**。可选择是否删除程序文件，以及是否删除
**本工具下载**的 7-Zip 引擎；本机原先安装的 7-Zip 永远不会被删除。

高级用户仍可运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
```

清理不依赖 `dist\`（换机残留同样卸得干净），卸完自检 0 残留。可选参数：

| 参数 | 用途 |
|------|------|
| `-Purge` | 顺带删除 `dist\` |
| `-PurgeEngine` | 顺带删除**本工具下载**的引擎（本机已有的引擎永远不动） |
| `-RestartExplorer` | 完成后重启资源管理器 |

## 默认打开方式

`.zip` / `.7z` / `.rar` 交给 `7zFM.exe`（7-Zip 文件管理器，含中文界面）。

`install.ps1` 会写 ProgId、`OpenWithProgids`，并清掉这些扩展名的 `UserChoice`——
Windows 只阻止程序**写** `UserChoice`（哈希校验），**删**是允许的；删掉后系统回落到我们写的
类默认值。如果系统仍然弹「选择应用」，跑一次：

```powershell
dist\7zipContext.exe --defaults     # 打开系统「默认应用」页，手动选 7-Zip
```

## 配置：dist\7zipContext.ini

改完保存，**下一次右键就生效**（不用重启资源管理器）；文件缺失会自动生成带注释的默认版。

```ini
[preview]
rootMax=20              ; 根目录最多显示几项，0 = 不限制
childrenPerFolder=3     ; 每个子文件夹展开几个，0 = 不展开子层
showFilesInSubfolders=0 ; 子层是否也显示文件
maxDepth=1              ; 展开层数 0/1/2
```

## 技术栈

- **7zipContext.Core** — 7z 后端、智能解压规则、回收站删除、**引擎只读定位器**
- **7zipContext.App** — CLI 执行体 + **设置面板**（无参启动），菜单唯一入口；**引擎获取与更新检查**
- **7zipContext.Shell** — Win11 `IExplorerCommand` 动词的 comhost 类库（7 个），由 dllhost 承载
- **7zipContext.Classic** — 经典菜单 `IContextMenu` handler，**跑在 explorer.exe 进程内**，负责内联目录树
- **7zipContext.Setup** — 图形化安装/卸载工具；安装到用户目录，复用同一套注册脚本与自检

v1 的 WPF 主窗口、预览弹窗、文件关联、`IPreviewHandler` 预览窗格**已全部移除**；
设置能力以轻量设置面板的形式保留（不再是 v1 那个主窗口）。
`install.ps1` 仍会清理老机器上的这些残留。

```
src/7zipContext.Core     逻辑与后端        src/7zipContext.Shell    现代菜单动词 comhost
src/7zipContext.App      CLI（7zipContext.exe）   src/7zipContext.Classic  经典菜单 handler
src/7zipContext.Setup    图形化安装卸载工具
package/          AppX 清单 + logo
scripts/          install / uninstall / publish / package / 7zipContext-menu
tests/            SmokeTest（Core 19）/ ComSmokeTest（动词 59）/ ComProbe / UiTest
assets/           7zipContext.ico、7zipContext.png
dist/             发布产物（也是已注册 AppX 包根，勿删）
```

## 开发

```powershell
dotnet build 7zipContext.sln -c Release
dotnet run --project tests/7zipContext.SmokeTest -c Release      # Core 19 项
.\tests\7zipContext.ComSmokeTest\bin\Release\net8.0-windows\7zipContext.ComSmokeTest.exe   # 59 项
.\tests\7zipContext.ComProbe\bin\Release\net8.0-windows\7zipContext.ComProbe.exe menu <file>  # 导出真实经典菜单树
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1            # 分项目发布再合并
```

`dotnet build 7zipContext.sln` 会连同 4 个测试工程一起编译。
测试走统一的引擎解析器；本机有 7-Zip 即可，无需额外准备二进制。

## 排障

- **右键点了没反应**：看 `%TEMP%\7zipContext-invoke-error.log`；`install.ps1` 的「菜单命令可执行」就是测这条。
- **菜单里没有 7zipContext**：Win11 紧凑菜单只显示 AppX 动词；经典 handler 要「显示更多选项」。
- **开始菜单里的 7zipContext 图标**：这是 AppX 包注册的应用入口，点开就是设置面板；
  不想要它可以从 `package\AppxManifest.xml` 移除 `Applications` 节点后重装（但会改变包形态，需自行验证）。
- **预览树是空的/只有一行灰字**：`%TEMP%\7zipContext-classic.log` 里有 `status=Timeout/Encrypted/Error`；
  超时说明包太大（250ms 硬超时），这是设计行为。
- **提示「未找到 7-Zip 引擎」**：跑 `install.ps1`；离线用 `-EnginePath <本机 7-Zip 目录>`。
- **下载引擎失败**：看 `%LOCALAPPDATA%\7zipContext\last-ensure.json` 的具体原因。
  GitHub 不可达时用 `-EngineSource <可达的官方安装包 URL>`，或 `-EnginePath` 走纯离线。
- **预览树位置或层级不对**：改 `dist\7zipContext.ini`，不用重装。
- **同名菜单项出现两次**：多半是机器上还装着另一款带右键菜单的压缩软件，它也注册了同名项
  （owner-draw 项，文本取不到）；关掉它的菜单集成即可。
- **装完菜单没变**：重启资源管理器；打包 COM 的菜单有缓存。
- **重装报 `0x80073D02`**：`dllhost` 还加载着 dist 里的 comhost，`install.ps1` 会清理并自动重试。
- **改完 `AppxManifest.xml` 必须重装**。

## 许可与致谢

- 本项目自身代码采用 [MIT](LICENSE)。
- 7-Zip 由 Igor Pavlov 开发，主要许可为 LGPL v2.1+，另含 BSD 2/3-clause 与 unRAR 许可限制。
  本仓库**不分发其二进制**，运行时按需从官方获取，来源、校验、源码获取方式与商标声明见
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

**关系声明**：7zipContext 是独立的第三方 shell 扩展，不是 7-Zip 官方项目，与 Igor Pavlov
及 7-Zip 项目无关联、未获其背书。「7-Zip」及其图标归各自权利人所有，本仓库使用它们仅为
标明所调用的压缩引擎。
