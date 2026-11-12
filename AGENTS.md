# 7zipContext · Agent 规则

**不是压缩软件，是「7-Zip 引擎 + 自建右键菜单」的 shell 增强层。** 公开项目，
普通用户用图形化安装包，开发者用源码脚本。完整定位与冻结决策见 `SPEC-v2.md`；v1 的
「主窗口 + 预览弹窗」形态已作废，但保留一个轻量 **设置面板**（开始菜单图标 / 无参启动 / `--settings` 打开）。
仓库：`JourneyHua/7zipContext`（公开）。

## 怎么跑

```powershell
dotnet build 7zipContext.sln -c Release
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -RestartExplorer
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
powershell -ExecutionPolicy Bypass -File scripts\package.ps1   # 生成图形化安装 ZIP
```

改了源码要去掉 `-SkipPublish`（会跑 `publish.ps1`）。安装完必须看到自检 **10/10 全绿**。
只改注册逻辑用 `-SkipPublish`；`publish.ps1` 会自己停/起 explorer。
离线安装用 `-EnginePath <本机 7-Zip 目录>`；GitHub 不可达时仅支持本机离线引擎，自定义 URL 已停用。

## 技术栈

C# / .NET 8。**仓库不带 7-Zip 本体**：安装阶段由 `EngineProvisioner` 探测本机/联网下载，
落到 `%LOCALAPPDATA%\7zipContext\engine\{版本}\`；运行时由 `EngineLocator` 只读
`engine.path` 定位 `7z.exe`（列表/测试）+ `7zG.exe`（压缩解压，自带进度窗和密码框）
+ `7zFM.exe`（默认打开方式）+ `7z.dll`。
菜单有两套：AppX 清单 + `IExplorerCommand`（Win11 现代菜单，8 个动词，含「用 7-Zip 打开」）、
`IContextMenu` comhost（经典菜单，内联目录树 + 同组解压/压缩/打开）。

## 目录

```
src/7zipContext.Core    后端与规则          src/7zipContext.Shell    现代菜单动词 comhost
src/7zipContext.App     CLI + 设置面板        src/7zipContext.Classic  经典菜单 handler（内联树）
src/7zipContext.Setup   图形化安装卸载工具     release/              安装 ZIP（不提交）
package/         AppxManifest        scripts/          install/uninstall/publish/package/7zipContext-menu
tests/           冒烟测试 + 诊断（ComProbe/UiTest）
dist/            发布产物 + 已注册 AppX 包根（勿删）       .workbuddy/  项目记忆（勿删）
```

## 红线（都是踩过的坑）

1. **现代菜单动词只能走 comhost + `com:SurrogateServer`**（dllhost 承载）。不要回退「自有 exe 做 LocalServer」——托管 CCW 跨进程派发会失效，Explorer 卡死。
2. **定位 `7zipContext.exe` 必须用 `Assembly.Location` 起算**。dllhost 里 `AppContext.BaseDirectory` 是空字符串 → 全部菜单项静默 `E_FAIL`。
3. **comhost 不能自包含发布**（NETSDK1128）；`publish.ps1` 必须**分项目独立发布再合并**，共用 `-o` 会互删运行时。单独发某个 comhost 工程时**绝不能 `-o dist`**，要发到 stage 再只拷该工程的产物。
4. **comhost 的 CLSID→类型映射不随增量编译刷新**。删类/改类名/改 GUID 后只跑 `dotnet publish`，实例化会 `0x80040111 CLASS_E_CLASSNOTAVAILABLE`，而注册表一切正常。**必须清 `obj/` `bin/` 全量重发**。
5. **explorer 进程内加载会锁 `dist\*.dll`**（`7zipContext.Classic` 的 CLR 跑在 explorer 里）。先完成编译、候选文件检查和备份，切换文件前才停相关宿主，结束后恢复资源管理器；不得先删 `dist`。
   2026-11-12：`install.ps1` 的经典 COM 激活自检改为独立子进程，退出后才能回滚文件，不能把 Classic 加载进安装宿主。
   旧版自检或其他探针若已把 DLL 加载进当前 PowerShell，仍需新开窗口；停止 explorer/dllhost 无法释放当前宿主的映像锁。
6. **经典菜单 handler 在 explorer 进程里跑**：任何异常都不许抛出去（全 try/catch + 落日志）。2026-11-12 审查增补：读取/配置/建树/排序放在受控的单个工作任务中；菜单等待最多 200ms，插入预览行检查 250ms 总预算，超时/超限降级成灰字，保留操作项。`rootMax=0` 不得绕过安全上限，首次图标查询不在菜单线程等待。
7. **改 `.ps1` 必须带 UTF-8 BOM**（5.1 按 GBK 解析中文会雪崩）。唯一的例外是 `7zipContext-menu.ps1`——它保持**纯 ASCII**。改完先用 `[Parser]::ParseFile` 预检语法。
8. **注册表键名含 `*`（`*\shellex\...`）必须用 `-LiteralPath` 或 .NET Registry API**，`Test-Path`/`Remove-Item` 会把 `*` 当通配符，静默查不到。
9. **改完菜单/清单/Shell 代码必须重装并重启资源管理器**，`dotnet build` 成功不算验证。
10. **验证菜单改动要跑真 COM 调用**：`7zipContext.ComSmokeTest`（75 项）+ `7zipContext.ComProbe menu <file>`（导出真实经典菜单树，能看清每条是谁加的）。
11. **不要删 `dist\`**（已注册 AppX 包根）；不要判 `git status` 干净就等于功能正常。
12. **v1/改名前的残留只删代码是清不掉的**：旧 `IPreviewHandler` CLSID、`ShellEx\{8895b1c6}`、
    旧 ProgId（`yaya.zip` 等）、旧 handler 键（`yayaPreview`/`yayaClassicSpike`）必须靠
    `--install` 与 `uninstall.ps1` 清。**这些历史名字要写字面量**——批量改名会把清理逻辑改成
    不存在的键名，于是永远清不掉。
13. **图标必须转成 DIB**：`GetIconInfo` 返回的是 DDB，直接塞 `MIIM_BITMAP` 画不出来（静默，
    没有错误码）。必须 `CopyImage(hbmColor, IMAGE_BITMAP, 0, 0, LR_CREATEDIBSECTION)`。
14. **经典菜单的解压动词由我们自己插**：AppX 的 `IExplorerCommand` 在经典菜单里只漏出
    「添加到 …」两条，解压四条不会出现（它们在紧凑菜单的「7zipContext &gt;」浮层里）。
    `PreviewMenu.QueryContextMenu` 负责插**六条**（4 解压 + 2 添加到）+ 预览树。
    「添加到」之所以由我们自己插：清单 `*` 组里的同名动词会被 Windows 漏到经典菜单顶部的
    「应用动作」区，和我们的块隔着十几个无关项，观感很差。所以清单只留解压4+快速压缩。
15. **C# 标识符不能以数字开头**：批量把 `yaya` 换成 `7zipContext` 会产生 `var 7zipContextCli`，
    编译直接崩。改名后必须全量 build 一次而不是只看 diff。
16. **AppX 父项图标 = `Square44x44Logo`**：紧凑菜单里「7zipContext &gt;」那一行的图标取自清单的
    VisualElements 资源，跟 `GetIcon` 无关。原来那张 `logo.png` 是 44×44 / 124 字节的空占位图，
    所以一直是空白。现由 `assets/icon-source.ico` 通过 `scripts/gen_icon.py` 统一导出，三个尺寸都要有。
17. **取图标别用假路径**：`SHGetFileInfoW(null, ...)` 直接返回 0；`SHGFI_USEFILEATTRIBUTES`
    配假路径 `"folder"` / `"file.txt"` 在本机也返回 0。要通用文件夹/文件图标就用真实路径
    （`%WINDIR%\System32` 与 `%WINDIR%\System32\drivers\etc\hosts`），假路径只当兜底。
18. **图标位图要从像素重建**：`GetIconInfo` 给的是 DDB，`CopyImage` 会丢 alpha →
    整张全透明（看着像"没图标"，且没有任何错误码）。正确做法 `GetDIBits` 取 32bpp 像素
    → `CreateDIBSection` → 拷入。判断标志：日志里 `alphaPixels` 计数不能是 0。
19. **仓库已公开**：不要在代码/文档/注释里写本机绝对路径（用户名目录、某台机器的安装目录）、
    内部代理地址或任何第三方压缩软件的产品名/内部文件名；测试工具一律用相对路径探测。
    第三方组件的来源、许可与商标声明集中在 `THIRD-PARTY-NOTICES.md`，改动引擎获取逻辑时同步更新。
20. **仓库/发布产物都不带 7-Zip 二进制**：`git ls-files` 里不能出现 `7z*.exe` / `7z.dll` / `Lang\`；
    三个 `.csproj` 不要再 `None Include ..\..\tools\...`。引擎由安装阶段单独获取。
21. **菜单路径绝不能联网**：`EngineLocator`（Core）只允许读 `engine.path` + 本地探测，
    零 `HttpClient`、零子进程。经典菜单 handler 在 explorer 进程内且有预览等待预算，
    一次 HTTP 就会卡死右键菜单。下载/解压/版本检查只放 `EngineProvisioner`（App，CLI 阶段）。
22. **WinExe 没有控制台**：`--ensure-engine` 等引擎命令由 `install.ps1` 以 `-WindowStyle Hidden`
    调用，**绝不能弹 MessageBox**（会挂住安装脚本）。结果写
    请求专属状态文件 + 退出码（安装脚本传 `--status-path`），脚本读文件展示；手动命令默认仍为
    `%LOCALAPPDATA%\7zipContext\last-ensure.json`。`--install/--uninstall` 隐藏注册失败同样不弹窗。
23. **别覆盖底层失败原因**：`DownloadLatest` 返回具体 reason，外层 `Ensure`/`Update`
    必须原样透传。曾出现过外层用「未能下载」笼统提示覆盖掉 `SHA256 不匹配 / 超时 / 代理拒绝`，
    导致完全无法排障。
24. **引擎优先级链**：自身缓存 → PATH（含 scoop `.shim` 解引用）→ 注册表 → 常见路径 → 下载。
    四件套（`7z.exe/7zG.exe/7zFM.exe/7z.dll`）齐全才算可用；本机已有时不联网。
    已有指针且元数据路径一致时**不要重写 `engine.json`**，否则每次重装都会把
    `origin=downloaded` 和 SHA256 抹成 `local`。
25. **下载必须校验**：GitHub Release API 的 asset `digest` 做 SHA256；
    自定义 `-EngineSource` 已停用；官方资产缺少 digest 即拒绝下载，不回退签名校验。
    不内置任何第三方镜像。
26. **设置窗口只在无参启动**：AppX 的 `<Application>` 节点会在开始菜单建一个图标，
    无参激活 → `OpenSettings()`。**右键动词与所有 `--*` 引擎命令绝不能弹窗**
    （install.ps1 以 `-WindowStyle Hidden` 调用，弹 MessageBox 会挂住安装脚本）。
    WinExe 没有 `Application` 实例，`OpenSettings()` 必须显式 `new Application` 再
    `ShowDialog()`，否则窗口一闪而过。
27. **WPF 项目的 `ImplicitUsings` 不含 `System.IO`**：新建 .cs 用到 `Path/File/Directory`
    必须显式 `using System.IO;`（踩过两次，一次 68 个 CS0103，一次 16 个）。
28. **ini 键名是跨程序集契约**：`AppSettings`（App）写、`PreviewSettings`（Classic，跑在
    explorer 内）读，四个键名/取值域必须一致；改任一侧都要同步另一侧。读写与默认模板统一在 Core.SettingsDocument；保存更新现有行并保留注释和未知键，
    不要用链式 `Replace`（注释里也有同名文字）。
29. **测试工程必须留在 `7zipContext.sln` 里**。曾漏加 4 个测试项目，
    于是 `dotnet build 7zipContext.sln` 显示成功、跑的还是几小时前的旧 exe，
    「59/59 全绿」是假象（旧二进制还在找已删除的 `dist\tools\7z.exe`）。
    改完测试代码后确认 exe 时间戳，别只看 build 结果。
30. **图形化安装包只是现有注册脚本的 GUI 外壳**：`7zipContext.Setup` 把 `payload\`
    复制到 `%LOCALAPPDATA%\7zipContext\app\`，再隐藏调用 `install.ps1 -SkipPublish`；
    不要另写一套注册逻辑。卸载同理复用 `uninstall.ps1`，本机已有 7-Zip 永远不删。
    ZIP 由 `scripts\package.ps1` 生成，仍不得携带 7-Zip 本体。
31. **分卷名和 7-Zip 打开都可配置**：分卷后缀来自 `[archive] splitFirstSuffixes/splitLaterSuffixes`，
    识别/去重/命名/删除统一走 `Core.ArchiveRules` 与 `SplitVolumeNames`，不要再写死 `.001`。
    2026-11-12 审查增补：配置只启用已验证格式，未知后缀不猜测解压入口；传统 `.z01` 需配套末尾 `.zip`，
    实际目标统一为 `.zip`，整组选中去重。自动删除另须通过家族/编号/引擎数量检查，传统 ZIP 暂时保留源文件。
    「用 7-Zip 打开」是独立动词（CLSID `B9A10009`），受 `openWithSevenZip` 控制；
    空选中查询要用 `VisibleWhenEmpty` 决定显隐，否则全局开关关闭时仍会闪一下。
32. **所有部署先取租约，使用同源文件事务**：`scripts/DeploymentFiles.cs` 由 Core 和 PowerShell 共用；GUI 文件操作放后台，脚本编译后才停宿主。`engine` 操作也遵守部署租约，内部锁顺序为部署租约 → 引擎锁；子任务继承部署凭据，长期交互窗口不得借用旧凭据。失败保留可恢复日志；文件恢复与注册恢复分开报告，不能重放旧 `--install` 来清除用户选择。

33. **CLI 退出码是公共契约**：0 全部成功，1 执行失败，2 参数错误，3 取消，4 部分成功/警告；不得提示错误后返回 0。多包结果必须汇总，源文件保留等后续步骤未完成返回 4。`--non-interactive` 禁止提示框/密码/目录选择并使用控制台引擎；回归 CLI 检查必须使用此参数，不能为测试改写用户引擎指针。

## 源码状态（不代表发布验收）

v2 当前源码包含：经典菜单内联目录树（规则见 `dist\7zipContext.ini`）、压缩/解压走 `7zG.exe`、
v1 主窗口/预览弹窗/旧文件关联方式/`IPreviewHandler` 已移除；设置面板及7zFM可选打开方式保留（`7zipContext.Preview` 工程已删）、
AppX 的 Preview 动词已退役（现代菜单 8 个动词，含「用 7-Zip 打开」）、自检 10 项（含引擎可用性）。
引擎改为运行时获取，仓库与 `dist\` 均不携带 7-Zip 二进制。
2026-11-11 追加轻量设置面板（`SettingsWindow.cs` / `AppSettings.cs`），由开始菜单图标打开；
追加 `7zipContext.Setup` 图形化安装卸载工具与 `release\7zipContext-Setup.zip` 打包链；
分卷后缀与「用 7-Zip 打开」改为 `[archive]` 配置（见红线 31），发布 v1.1.1。

基线：SmokeTest 32/32、ComSmokeTest 75/75、install 自检 10/10（新增「7-Zip 引擎可用」）。
同名菜单项重复通常来自机器上另一款压缩软件，本项目的经典菜单动词已全部改为自插以避开冲突。

34. **更新缓存绑定引擎目录与版本**：UpdateCheckStore 在现有引擎锁中执行；请求前原子保存尝试，成功缓存24小时，失败指数退避15分钟至6小时。离线历史结果不得伪装新检查成功；时间及请求函数可注入，测试不得联网。

2026-11-12 卸载审查决定：-Purge/-PurgeEngine 删除失败必须报告路径并返回非零退出码，不得提示全部完成；注册残留时暂不删除程序及引擎，保留重试条件。引擎逐目录核验 engine-records 的路径绑定来源（兼容匹配的旧 engine.json），仅删除确认 downloaded 的目录；本机、未知或冲突来源保留并列出，不清空整个 engine 根目录。删除目录成功后才清理对应来源记录；仅当当前指针目标已删除，才清理指针及相关查询状态。锁失败、重解析路径及目录越界均拒绝删除。测试命令：powershell -NoProfile -ExecutionPolicy Bypass -File tests\purge-engine.ps1，仅操作自行创建的临时目录，不执行真实卸载。
2026-11-12 混选规则：ArchiveSelection 统一生成解压目标，保留选中顺序；传统 ZIP 的 .z01 归一至配套 .zip，后续卷不独立执行，路径按完整路径及忽略大小写去重。普通压缩包和不同分卷组共同处理。现代/经典菜单有至少一个支持目标即可显示解压项，只传有效目标；纯普通文件或仅后续卷不显示解压。直接 CLI 调用仍报告不存在或不支持的输入，并汇总有效目标执行结果，不静默视为全部成功。未知分卷格式仍不猜测入口，自动删除的安全约束不变。
2026-11-12 配置规则：Core.SettingsDocument 是设置面板、预览菜单和 ArchiveRules 的共享读写模型及唯一默认模板。缺失键使用默认值；明确存在但为空的 splitFirstSuffixes / splitLaterSuffixes 表示空列表，不恢复默认。设置面板允许清空，只有点击“恢复默认”才恢复默认列表。空首卷列表关闭后缀启用的分卷入口；普通压缩包（包括传统ZIP的.zip入口）及内建后续卷安全保护仍保留，自动删除约束不变。

保存重新读取磁盘内容，只更新已知键；保留未知键、未知段落、独立/行内注释及已有UTF-8 BOM和换行风格。旧键名splitFirst/splitLater继续支持，重复已知键同步更新，读取仍以最后一个有效设置为准。缺失的已知键补入相应段落，已有用户扩展不会因“恢复默认”丢失。菜单缺失配置时只用内存默认值，不自动写文件；首次创建由设置面板显式保存完成。未提供多进程配置编辑事务，已知字段同时修改仍以最后保存者为准。
2026-11-12 树与诊断资源审查决定：保留TreeNode及诊断命令接口。未暴露子节点列表的建树路径使用字典索引；Children/RootEntries一旦被调用方获取，后续插入会重建该节点索引，以兼容持有List后直接Clear/替换等操作。文件/目录同名及大小写区分语义不变。

Classic菜单日志和Shell调用错误日志统一使用BoundedLog：主文件最多1 MiB，保留一份同上限的.1备份，每条最多4096字符后附截断标记；旧超大文件在下一次写入时清理。跨进程独占.lock文件协调，不等待重试，忙或写入失败时跳过该条日志。依赖文件系统调用的日志不是硬实时操作，旧版本不参与此协议。

诊断工具共享NativeResources，PIDL/COM指针/RCW/HMENU/临时缓冲区按作用域释放，早退及异常仍清理；UI诊断计时器在退出前等待自身回调完成，回调不再同步Invoke界面线程。无界面回归只创建并销毁未显示菜单对象；本次不运行真实COM激活、弹出菜单、截图或菜单命令。
2026-11-12 图标规则：以assets/icon-source.ico为唯一编辑源，保留现有16/32/48/256像素图像。scripts/gen_icon.py统一导出assets/7zipContext.ico、256像素PNG及package下44/150/50像素的AppX PNG；不重新设计图标、不下载素材、不修改dist。使用Python 3.10+及scripts/requirements-icons.txt固定的Pillow版本。python scripts/gen_icon.py生成全部资源；追加--check只比较不写入，缺失或过期返回1；python tests/test_icons.py执行隔离验证。生成结果不独立编辑，详细流程见assets/README.md。
## 文档与验收依据

现行规格以根目录SPEC-v2.md为准，技术变更摘要见docs/CHANGELOG-AUDIT.md。docs/RELEASE-VALIDATION.md区分自动化证据、历史证据和未执行项目；不得把源码构建、未显示菜单或旧安装日志记成当前产物系统集成通过。安装包发布与系统验收状态单独记录。
