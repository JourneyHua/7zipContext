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
| **右键 · 分卷压缩包 `.001`** | 单选 `xxx.7z.001`、多选 `.001+.002` 都显示解压；执行只读首卷，不重复解压 |
| **右键 · 用 7-Zip 打开(O)** | 所有文件/文件夹都显示，直接交给 `7zFM.exe`；可在设置里关闭 |
| **经典菜单 · 压缩文件预览** | 菜单里直接铺开压缩包结构，不开窗、不解压 |

分卷「并删除」只处理同一编号家族，不混入同名其他格式。已知数字卷与 `.part1.rar`
家族需编号连续、命名一致，且数量与引擎报告一致才会移入回收站；超过 999 卷不再截断。
编号空缺、传统分卷 ZIP、未知自定义命名、引擎不报告数量或组成员在解压期间变化时，
解压成功仍保留全部源文件，并提示原因。自定义后缀用于识别入口，不代表允许自动删除。

预览树的默认规则：根目录最多显示 20 项，文件夹在前；每个根文件夹只展开前 3 个
子文件夹，子项紧跟自己的父目录；子层不列文件。条数上限与展开层数都能在
`dist\7zipContext.ini` 里调整。普通 `.zip` 用 .NET 直读中央目录；检测到同名 `.z01` 的传统分卷 ZIP 使用引擎读取。

预览有独立安全上限：单个入口文件 512 MiB、列表 1 万条、树节点 2 万个、菜单 200 行，
路径最多 1024 字符和 32 层，ZIP 中央目录最多 8 MiB。ZIP64 或超限预览会省略。
读取在最多一个工作任务中执行，菜单等待最多 200ms，插入预览行时检查 250ms 总预算；
超限显示「预览已省略」，压缩、解压和打开仍可使用。配置 `rootMax=0` 也不能关闭这些保护。
首次图标尚未就绪时先显示文字，后续菜单可使用缓存图标。

两套菜单各管各的：

- **Win11 现代菜单**（右键第一层）：8 个动词，没有预览
- **经典菜单**（右键 → 显示更多选项 / `Shift+F10`）：同组解压/压缩/打开 + 内联预览树

交互压缩/解压优先使用 `7zG.exe` 的进度和密码框；控制台回退可有密码提示，非交互模式不弹窗。另有设置面板和安装卸载界面。

## 安装

### 普通用户：图形化安装包

先安装 [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0)（已有则跳过）。主程序、安装器和菜单扩展共用系统运行库，ZIP 不附带 .NET；缺少时需手动安装。

1. 下载并**完整解压** [7zipContext-Setup.zip](https://github.com/JourneyHua/7zipContext/releases/latest)，不要直接在压缩软件窗口里运行。
2. 双击 `7zipContext.Setup.exe`，点击 **「安装 / 修复」**。
3. 等待安装与自检完成，再右键文件或文件夹即可使用。

安装器的「设为默认打开方式」默认不勾选。勾选后，安装成功会打开系统「默认应用」，
由你逐项为 `.zip` / `.7z` / `.rar` 选择 7-Zip；不勾选则保留现有默认程序。

程序安装到 `%LOCALAPPDATA%\7zipContext\app\`，不需要管理员权限；安装完成后可以删除
下载的 ZIP 和解压目录。已安装目录里也保留了一份图形化安装卸载工具。
若本机没有完整的 7-Zip，安装器会自动从官方发布页下载、校验并解包。
安装期间资源管理器会短暂重启，这是正常现象。

安装器会在后台准备文件、备份和引擎，通过后才切换程序文件；失败时恢复旧文件，恢复不完整则保留备份并显示位置。
下次安装会检查未完成的文件事务。系统注册的恢复结果单独报告，不把文件恢复当作注册也已恢复。
安装、卸载、发布、打包与引擎操作共用部署互斥，避免同时替换文件。成功或失败提示出现前会先恢复资源管理器。
从已安装工具发起修复时保留正在运行的安装器自身；升级安装器请使用新下载的完整 ZIP。

需要 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（自检会提示）。

### 高级用户：从源码安装

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -RestartExplorer
```

脚本依次完成：发布 → **准备 7-Zip 引擎** → 注册两套菜单 → 清理旧版本残留 → **自检 10 项**。
生成图形化安装包则运行 `scripts\package.ps1`，产物为 `release\7zipContext-Setup.zip`。

`publish.ps1` 先在独立目录编译、合并和备份，再逐文件切换，保持已注册的 `dist` 根目录不变。
源码安装会保留发布备份直到自检通过；中途失败恢复旧文件，恢复失败保留日志与备份。
经典 COM 激活自检在独立子进程执行，避免安装宿主锁住 DLL。打包 ZIP 也会先完整生成候选文件，再替换旧 ZIP。

可选参数：

| 参数 | 用途 |
|------|------|
| `-SkipPublish` | 跳过编译发布（只改注册逻辑时重跑更快） |
| `-RestartExplorer` | 完成后重启资源管理器，菜单立即可见 |
| `-EnginePath <目录>` | 离线安装：指向本机已有的 7-Zip 目录（需含四件套） |
| `-EngineSource <URL>` | 已停用，传入即报错；请使用官方源或本机离线引擎 |
| `-SkipEngineCheck` | 跳过「更新检查」提示（不影响引擎准备） |

## 设置面板

AppX 包在开始菜单注册了一个 **7zipContext** 图标，点它（或直接双击
`dist\7zipContext.exe`、运行 `dist\7zipContext.exe --settings`）会打开设置面板，五个页签：

| 页签 | 内容 |
|------|------|
| **预览规则** | 可视化编辑根目录条数、每层展开数、是否显示子层文件、展开层数；「保存」后下一次右键生效，可「恢复默认」或直接打开 ini |
| **分卷与打开** | 编辑首卷/后续卷后缀，开关「用 7-Zip 打开」菜单 |
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
   先在 `engine-records\` 保存与引擎目录绑定的版本、来源、SHA-256、安装时间，再原子替换
   `engine.path` 切换当前目录。`engine.json` 保留为兼容快照，读取时不能把其他目录的记录当成当前状态。

准备、重新检测、检查更新、升级及可选打开方式注册共用跨进程引擎锁；同一设置窗口会统一禁用
相关按钮，其他进程正在操作时提示稍后重试。切换后刷新可选打开方式中的引擎路径，保留用户默认程序。
安装脚本为每次请求指定独立状态文件，避免并发安装互相读取结果。手动修改 `engine.path` 回滚时，
设置面板会优先读取该目录对应的历史记录；引擎目录本身仍需四件套齐全。

**菜单运行时只读 `engine.path`**：零网络、零子进程、微秒级。经典菜单 handler 跑在
explorer.exe 进程内，预览有等待预算且不在菜单线程做阻塞读取；下载永远只在安装阶段发生。

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

安装会将 `7zFM.exe`（7-Zip 文件管理器，含中文界面）添加到 `.zip` / `.7z` / `.rar` 的可选打开方式。

`install.ps1` 只写 ProgId 和 `OpenWithProgids`，不覆盖扩展名的默认程序，也不删除 `UserChoice`。
安装、升级和重新注册均保留用户选择。若需要设为默认，可勾选安装器的可选项，或运行：

```powershell
dist\7zipContext.exe --defaults     # 打开系统「默认应用」页，手动选 7-Zip
```

## 配置：dist\7zipContext.ini

改完保存，**下一次右键就生效**（不用重启资源管理器）；文件缺失会自动生成带注释的默认版。

```ini
[preview]
rootMax=20              ; 根目录最多显示几项，0 = 不自定义限制；仍有总计 200 行安全上限
childrenPerFolder=3     ; 每个子文件夹展开几个，0 = 不展开子层
showFilesInSubfolders=0 ; 子层是否也显示文件
maxDepth=1              ; 展开层数 0/1/2

[archive]
splitFirstSuffixes=.001,.z01   ; 已支持的首卷候选；.z01 需要配套末尾 .zip
splitLaterSuffixes=.002,.z02   ; 分卷后续后缀，逗号分隔
openWithSevenZip=1             ; 是否对所有文件/文件夹显示「用 7-Zip 打开」
```

分卷规则说明：

- 数字分卷从 `.001` 进入；配置的 `.part1.rar`（含补零形式）也可作为入口，后续卷单独选中不显示解压。
- 传统分卷 ZIP 的 `.z01` 需要同目录下的末尾 `.zip`；两者都规范到 `.zip`，整组选中只执行一次。缺少 `.zip` 时不提供 `.z01` 解压入口。
- 未验证的自定义后缀不会仅因加入配置就显示解压，可使用独立的「用 7-Zip 打开」（需启用该开关）。
- 自动删除遵守前述家族、连续编号与引擎数量核对；传统 ZIP 和不确定的组保留源文件并提示。
- 「用 7-Zip 打开」是独立菜单项，不改变智能解压识别规则；关闭 `openWithSevenZip` 后两套菜单都不显示它。
- 设置面板里对应「分卷与打开」页签，改完保存下一次右键生效。

## 技术栈

- **7zipContext.Core** — 7z 后端、智能解压规则、回收站删除、**引擎只读定位器**、**可配置分卷规则**
- **7zipContext.App** — CLI 执行体 + **设置面板**（无参启动），菜单唯一入口；**引擎获取与更新检查**
- **7zipContext.Shell** — Win11 `IExplorerCommand` 动词的 comhost 类库（8 个），由 dllhost 承载
- **7zipContext.Classic** — 经典菜单 `IContextMenu` handler，**跑在 explorer.exe 进程内**，负责内联目录树
- **7zipContext.Setup** — 图形化安装/卸载工具；安装到用户目录，复用同一套注册脚本与自检

v1 的 WPF 主窗口、预览弹窗、旧文件关联和 `IPreviewHandler` 预览窗格已移除；
当前仅注册可选打开方式，默认应用由用户在系统设置选择。
设置能力以轻量设置面板的形式保留（不再是 v1 那个主窗口）。
`install.ps1` 仍会清理老机器上的这些残留。

```
src/7zipContext.Core     逻辑与后端        src/7zipContext.Shell    现代菜单动词 comhost
src/7zipContext.App      CLI（7zipContext.exe）   src/7zipContext.Classic  经典菜单 handler
src/7zipContext.Setup    图形化安装卸载工具
package/          AppX 清单 + logo
scripts/          install / uninstall / publish / package / 7zipContext-menu
tests/            SmokeTest（Core 32）/ ComSmokeTest（动词 75）/ ComProbe / UiTest
assets/           7zipContext.ico、7zipContext.png
dist/             发布产物（也是已注册 AppX 包根，勿删）
```

## CLI 解压目标

`--extract-to <压缩包...>` 会选择一次目录并解压全部选中的目标；自动化调用请用
`--extract-to <压缩包...> --destination <目录> --non-interactive`。旧的隐式第二参数目标写法已取消，
避免多选压缩包时把第二个文件误当作目录。

## CLI 退出码

所有命令遵守同一退出码协议，脚本应等待进程退出后检查 ExitCode，不能只根据提示文字判断成功。

| 退出码 | 含义 |
|---|---|
| `0` | 全部成功；打开设置/文件管理器等 GUI 命令只确认窗口或程序已启动/正常关闭，不代表后续用户操作成功 |
| `1` | 执行失败，例如压缩包损坏、引擎缺失、启动失败、注册或状态文件写入失败；部署忙也返回此码 |
| `2` | 参数错误，例如缺少输入、路径不存在、未知/重复选项、无有效分卷入口，或引擎报告命令行语法错误 |
| `3` | 用户取消，包括目录选择、密码输入、引擎取消和操作取消异常 |
| `4` | 部分成功或引擎警告，包括多包有成功也有失败/取消/无效输入、解压成功但源文件保留、压缩完成但打开结果目录失败 |

多包全部未成功时，失败优先于参数错误，参数错误优先于取消；任一引擎警告均返回 `4`。压缩输入包含不存在的路径时整条命令返回 `2`，不会静默生成缺少输入的压缩包。解压多包会继续处理有效目标，最后汇总退出码；分卷入口去重后与普通压缩包共同处理，不因选中首卷而跳过普通包。

添加 `--non-interactive` 可禁止本程序提示框、目录选择和密码输入，并强制压缩/解压使用控制台引擎，不启动 7zG 进度窗。此模式的 `--extract-to` 必须带 `--destination`；遇到需要密码的压缩包返回 `1`，不会等待输入。`--quick-zip` 不打开结果目录；`--settings`、`--open`、`--defaults`、`--open-with-7zip` 及裸路径打开界面不适用于此模式，返回 `2`。它不把安装/卸载等命令变成只读操作。

引擎命令仍默认不弹窗，只有显式 `--show` 才显示结果；`--show` 不能与 `--non-interactive` 同用。参数校验失败返回 `2`，不会运行引擎或更新状态文件。引擎已就绪但关联/元数据刷新有警告返回 `4`，JSON 的 `warnings` 标记为 true；安装器确认 `ok=true` 后显示警告，继续正式注册和自检。更新查询离线返回 `1`，即使已有引擎仍可用。原始引擎退出码 `0/1/7/255` 分别映射为 `0/4/2/3`，其他非零码映射为 `1`。

WinExe 不能保证从调用终端显示 stdout/stderr；自动化请使用等待退出的进程对象。引擎命令可额外通过 `--status-path <文件>` 获取 JSON。PowerShell 示例：

```powershell
$process = Start-Process -FilePath '.\dist\7zipContext.exe' -ArgumentList '--test "D:\archives\example.zip" --non-interactive' -Wait -PassThru
$process.ExitCode
```

含空格的输入路径需要按调用环境正确引用；以 `--` 开头的文件名请使用完整路径。
## 开发

技术规格见 [SPEC-v2.md](SPEC-v2.md)，测试流程见[发布验证](docs/RELEASE-VALIDATION.md)。版本更新说明见 [GitHub Releases](https://github.com/JourneyHua/7zipContext/releases)。

```powershell
dotnet build 7zipContext.sln -c Release
dotnet run --project tests/7zipContext.SmokeTest -c Release      # Core 测试
.\tests\7zipContext.ComSmokeTest\bin\Release\net8.0-windows\7zipContext.ComSmokeTest.exe   # COM 菜单测试
.\tests\7zipContext.ComProbe\bin\Release\net8.0-windows\7zipContext.ComProbe.exe menu <file>  # 导出真实经典菜单树
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1            # 分项目发布再合并
```

`dotnet run --project tests/7zipContext.RegressionTest -c Release` 验证配置契约、管道防阻塞、取消清理和预览错误降级。

回归测试全程使用无界面引擎调用，包括真实多包 CLI 解压和退出码检查；保留 `-- --headless` 兼容参数。包含真实传统分卷 ZIP 预览，以及隔离引擎夹具模拟警告、取消、密码重试和失败传播。
无界面回归也会创建并销毁未显示的原生菜单对象，检查编号范围、默认动作查询、插入失败及不可执行的预览行；不会弹出菜单或执行有效菜单动作。

`powershell -NoProfile -ExecutionPolicy Bypass -File tests\deployment-files.ps1` 在临时目录验证 Windows PowerShell 5.1 使用的同一份部署后端，不执行安装、注册或资源管理器控制。

`dotnet build 7zipContext.sln` 会连同 5 个测试工程一起编译。
测试走统一的引擎解析器；本机有 7-Zip 即可，无需额外准备二进制。

## 排障

- **右键点了没反应**：看 `%TEMP%\7zipContext-invoke-error.log`；`install.ps1` 的「菜单命令可执行」就是测这条。
- **菜单里没有 7zipContext**：Win11 紧凑菜单只显示 AppX 动词；经典 handler 要「显示更多选项」。
- **开始菜单里的 7zipContext 图标**：这是 AppX 包注册的应用入口，点开就是设置面板；
  不想要它可以从 `package\AppxManifest.xml` 移除 `Applications` 节点后重装（但会改变包形态，需自行验证）。
- **预览树是空的/只有一行灰字**：查看 `%TEMP%\7zipContext-classic.log` 中的 `status`；
  `Timeout/Omitted` 表示耗时、规模超限或已有读取任务，预览会省略，操作项仍可使用。
- **提示「未找到 7-Zip 引擎」**：跑 `install.ps1`；离线用 `-EnginePath <本机 7-Zip 目录>`。
- **下载引擎失败**：看 `%LOCALAPPDATA%\7zipContext\last-ensure.json` 的具体原因。
  GitHub 不可达时用 `-EnginePath <本机引擎目录>` 走纯离线；自定义 URL 已停用。
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
