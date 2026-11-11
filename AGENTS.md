# 7zipContext · Agent 规则

**不是压缩软件，是「7-Zip 引擎 + 自建右键菜单」的 shell 增强层。** 自用便携版。
完整定位与冻结决策见 `SPEC-v2.md`；v1 的「主窗口 + 预览弹窗」形态已作废，
但保留一个轻量 **设置面板**（开始菜单图标 / 无参启动 / `--settings` 打开）。
仓库：`JourneyHua/7zipContext`（公开）。

## 怎么跑

```powershell
dotnet build 7zipContext.sln -c Release
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -RestartExplorer
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
```

改了源码要去掉 `-SkipPublish`（会跑 `publish.ps1`）。安装完必须看到自检 **10/10 全绿**。
只改注册逻辑用 `-SkipPublish`；`publish.ps1` 会自己停/起 explorer。
离线安装用 `-EnginePath <本机 7-Zip 目录>`；GitHub 不可达用 `-EngineSource <URL>`。

## 技术栈

C# / .NET 8。**仓库不带 7-Zip 本体**：安装阶段由 `EngineProvisioner` 探测本机/联网下载，
落到 `%LOCALAPPDATA%\7zipContext\engine\{版本}\`；运行时由 `EngineLocator` 只读
`engine.path` 定位 `7z.exe`（列表/测试）+ `7zG.exe`（压缩解压，自带进度窗和密码框）
+ `7zFM.exe`（默认打开方式）+ `7z.dll`。
菜单有两套：AppX 清单 + `IExplorerCommand`（Win11 现代菜单，7 个动词）、
`IContextMenu` comhost（经典菜单，内联目录树）。

## 目录

```
src/7zipContext.Core    后端与规则          src/7zipContext.Shell    现代菜单动词 comhost
src/7zipContext.App     CLI + 设置面板        src/7zipContext.Classic  经典菜单 handler（内联树）
package/         AppxManifest        scripts/          install/uninstall/publish/7zipContext-menu
tests/           冒烟测试 + 诊断（ComProbe/UiTest）
dist/            发布产物 + 已注册 AppX 包根（勿删）       .workbuddy/  项目记忆（勿删）
```

## 红线（都是踩过的坑）

1. **现代菜单动词只能走 comhost + `com:SurrogateServer`**（dllhost 承载）。不要回退「自有 exe 做 LocalServer」——托管 CCW 跨进程派发会失效，Explorer 卡死。
2. **定位 `7zipContext.exe` 必须用 `Assembly.Location` 起算**。dllhost 里 `AppContext.BaseDirectory` 是空字符串 → 全部菜单项静默 `E_FAIL`。
3. **comhost 不能自包含发布**（NETSDK1128）；`publish.ps1` 必须**分项目独立发布再合并**，共用 `-o` 会互删运行时。单独发某个 comhost 工程时**绝不能 `-o dist`**，要发到 stage 再只拷该工程的产物。
4. **comhost 的 CLSID→类型映射不随增量编译刷新**。删类/改类名/改 GUID 后只跑 `dotnet publish`，实例化会 `0x80040111 CLASS_E_CLASSNOTAVAILABLE`，而注册表一切正常。**必须清 `obj/` `bin/` 全量重发**。
5. **explorer 进程内加载会锁 `dist\*.dll`**（`7zipContext.Classic` 的 CLR 跑在 explorer 里），发布前必须停 explorer + 清掉加载了本项目的 dllhost，发布后再拉起（`publish.ps1` 的 `Stop-DistLockers` 已做，删除失败会重试）。
   **更隐蔽的一层**：`install.ps1` 自检会实例化经典菜单的**进程内 COM**（`InprocServer32`），
   DLL 会一直锁在那个 PowerShell 宿主进程里，杀 explorer/dllhost 都无效。
   所以「同一个 PowerShell 会话里先 install 再 publish」必然失败，必须**新开窗口**跑，
   或直接跑 `install.ps1`（它会在实例化 COM 之前完成发布）。
6. **经典菜单 handler 在 explorer 进程里跑**：任何异常都不许抛出去（全 try/catch + 落日志）、不许做耗时工作（列目录 **250ms 硬超时**，超了降级成一行灰字）。
7. **改 `.ps1` 必须带 UTF-8 BOM**（5.1 按 GBK 解析中文会雪崩）。唯一的例外是 `7zipContext-menu.ps1`——它保持**纯 ASCII**。改完先用 `[Parser]::ParseFile` 预检语法。
8. **注册表键名含 `*`（`*\shellex\...`）必须用 `-LiteralPath` 或 .NET Registry API**，`Test-Path`/`Remove-Item` 会把 `*` 当通配符，静默查不到。
9. **改完菜单/清单/Shell 代码必须重装并重启资源管理器**，`dotnet build` 成功不算验证。
10. **验证菜单改动要跑真 COM 调用**：`7zipContext.ComSmokeTest`（59 项）+ `7zipContext.ComProbe menu <file>`（导出真实经典菜单树，能看清每条是谁加的）。
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
    所以一直是空白。真图由 `assets\7zipContext.ico` 生成（PIL，见提交记录），三个尺寸都要有。
17. **取图标别用假路径**：`SHGetFileInfoW(null, ...)` 直接返回 0；`SHGFI_USEFILEATTRIBUTES`
    配假路径 `"folder"` / `"file.txt"` 在本机也返回 0。要通用文件夹/文件图标就用真实路径
    （`%WINDIR%\System32` 与 `%WINDIR%\System32\drivers\etc\hosts`），假路径只当兜底。
18. **图标位图要从像素重建**：`GetIconInfo` 给的是 DDB，`CopyImage` 会丢 alpha →
    整张全透明（看着像"没图标"，且没有任何错误码）。正确做法 `GetDIBits` 取 32bpp 像素
    → `CreateDIBSection` → 拷入。判断标志：日志里 `alphaPixels` 计数不能是 0。
19. **仓库已公开**：不要在代码/文档/注释里写本机绝对路径（`C:\Users\...`、某台机器的安装目录）、
    内部代理地址或任何第三方压缩软件的产品名/内部文件名；测试工具一律用相对路径探测。
    第三方组件的来源、许可与商标声明集中在 `THIRD-PARTY-NOTICES.md`，改动引擎获取逻辑时同步更新。
20. **仓库/发布产物都不带 7-Zip 二进制**：`git ls-files` 里不能出现 `7z*.exe` / `7z.dll` / `Lang\`；
    三个 `.csproj` 不要再 `None Include ..\..\tools\...`。引擎由安装阶段单独获取。
21. **菜单路径绝不能联网**：`EngineLocator`（Core）只允许读 `engine.path` + 本地探测，
    零 `HttpClient`、零子进程。经典菜单 handler 在 explorer 进程内且有 250ms 硬超时，
    一次 HTTP 就会卡死右键菜单。下载/解压/版本检查只放 `EngineProvisioner`（App，CLI 阶段）。
22. **WinExe 没有控制台**：`--ensure-engine` 等引擎命令由 `install.ps1` 以 `-WindowStyle Hidden`
    调用，**绝不能弹 MessageBox**（会挂住安装脚本）。结果写
    `%LOCALAPPDATA%\7zipContext\last-ensure.json` + 退出码，脚本读文件展示。
23. **别覆盖底层失败原因**：`DownloadLatest` 返回具体 reason，外层 `Ensure`/`Update`
    必须原样透传。曾出现过外层用「未能下载」笼统提示覆盖掉 `SHA256 不匹配 / 超时 / 代理拒绝`，
    导致完全无法排障。
24. **引擎优先级链**：自身缓存 → PATH（含 scoop `.shim` 解引用）→ 注册表 → 常见路径 → 下载。
    四件套（`7z.exe/7zG.exe/7zFM.exe/7z.dll`）齐全才算可用；本机已有时不联网。
    已有指针且元数据路径一致时**不要重写 `engine.json`**，否则每次重装都会把
    `origin=downloaded` 和 SHA256 抹成 `local`。
25. **下载必须校验**：GitHub Release API 的 asset `digest` 做 SHA256；
    自定义 `-EngineSource` 无 digest 时改验 Authenticode，都不通过就拒绝执行。
    不内置任何第三方镜像。
26. **设置窗口只在无参启动**：AppX 的 `<Application>` 节点会在开始菜单建一个图标，
    无参激活 → `OpenSettings()`。**右键动词与所有 `--*` 引擎命令绝不能弹窗**
    （install.ps1 以 `-WindowStyle Hidden` 调用，弹 MessageBox 会挂住安装脚本）。
    WinExe 没有 `Application` 实例，`OpenSettings()` 必须显式 `new Application` 再
    `ShowDialog()`，否则窗口一闪而过。
27. **WPF 项目的 `ImplicitUsings` 不含 `System.IO`**：新建 .cs 用到 `Path/File/Directory`
    必须显式 `using System.IO;`（踩过两次，一次 68 个 CS0103，一次 16 个）。
28. **ini 键名是跨程序集契约**：`AppSettings`（App）写、`PreviewSettings`（Classic，跑在
    explorer 内）读，四个键名/取值域必须一致；改任一侧都要同步另一侧。保存用逐行重建，
    不要用链式 `Replace`（注释里也有同名文字）。
29. **测试工程必须留在 `7zipContext.sln` 里**。曾漏加 4 个测试项目，
    于是 `dotnet build 7zipContext.sln` 显示成功、跑的还是几小时前的旧 exe，
    「59/59 全绿」是假象（旧二进制还在找已删除的 `dist\tools\7z.exe`）。
    改完测试代码后确认 exe 时间戳，别只看 build 结果。

## 当前状态

v2 全部落地：经典菜单内联目录树（规则见 `dist\7zipContext.ini`）、压缩/解压走 `7zG.exe`、
v1 主窗口/预览弹窗/文件关联/`IPreviewHandler` 全部移除（`7zipContext.Preview` 工程已删）、
AppX 的 Preview 动词已退役（现代菜单 7 个动词）、自检 10 项（含引擎可用性）。
引擎改为运行时获取，仓库与 `dist\` 均不携带 7-Zip 二进制。
2026-11-11 追加轻量设置面板（`SettingsWindow.cs` / `AppSettings.cs`），由开始菜单图标打开。

基线：SmokeTest 19/19、ComSmokeTest 59/59、install 自检 10/10（新增「7-Zip 引擎可用」）。
同名菜单项重复通常来自机器上另一款压缩软件，本项目的经典菜单动词已全部改为自插以避开冲突。
