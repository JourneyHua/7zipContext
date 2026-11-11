# 7zipContext · Agent 规则

**不是压缩软件，是「7-Zip 引擎 + 自建右键菜单」的 shell 增强层。** 自用便携版。
完整定位与冻结决策见 `SPEC-v2.md`；v1 的「主窗口 + 预览弹窗」形态已作废。
仓库：`JourneyHua/7zipContext`（公开）。

## 怎么跑

```powershell
dotnet build 7zipContext.sln -c Release
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -RestartExplorer
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
```

改了源码要去掉 `-SkipPublish`（会跑 `publish.ps1`）。安装完必须看到自检 **9/9 全绿**。
只改注册逻辑用 `-SkipPublish`；`publish.ps1` 会自己停/起 explorer。

## 技术栈

C# / .NET 8。引擎是打包的 `tools\7z.exe`（列表/测试，要 stdout）+ `tools\7zG.exe`
（**压缩与解压**，自带进度窗和密码框）+ `tools\7zFM.exe`（默认打开方式，配 `Lang\zh-cn.txt`）
+ `tools\7z.dll`（LGPL）。
菜单有两套：AppX 清单 + `IExplorerCommand`（Win11 现代菜单，7 个动词）、
`IContextMenu` comhost（经典菜单，内联目录树）。

## 目录

```
src/7zipContext.Core    后端与规则          src/7zipContext.Shell    现代菜单动词 comhost
src/7zipContext.App     CLI 执行体（无窗口） src/7zipContext.Classic  经典菜单 handler（内联树）
package/         AppxManifest        scripts/          install/uninstall/publish/7zipContext-menu
tests/           冒烟测试 + 诊断（ComProbe/UiTest）
dist/            发布产物 + 已注册 AppX 包根（勿删）       .workbuddy/  项目记忆（勿删）
```

## 红线（都是踩过的坑）

1. **现代菜单动词只能走 comhost + `com:SurrogateServer`**（dllhost 承载）。不要回退「自有 exe 做 LocalServer」——托管 CCW 跨进程派发会失效，Explorer 卡死。
2. **定位 `7zipContext.exe` 必须用 `Assembly.Location` 起算**。dllhost 里 `AppContext.BaseDirectory` 是空字符串 → 全部菜单项静默 `E_FAIL`。
3. **comhost 不能自包含发布**（NETSDK1128）；`publish.ps1` 必须**分项目独立发布再合并**，共用 `-o` 会互删运行时。单独发某个 comhost 工程时**绝不能 `-o dist`**，要发到 stage 再只拷该工程的产物。
4. **comhost 的 CLSID→类型映射不随增量编译刷新**。删类/改类名/改 GUID 后只跑 `dotnet publish`，实例化会 `0x80040111 CLASS_E_CLASSNOTAVAILABLE`，而注册表一切正常。**必须清 `obj/` `bin/` 全量重发**。
5. **explorer 进程内加载会锁 `dist\*.dll`**（`7zipContext.Classic` 的 CLR 跑在 explorer 里），发布前必须 `Stop-Process explorer`，发布后再拉起。
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
    第三方组件的许可与商标声明集中在 `THIRD-PARTY-NOTICES.md`，改动随包二进制时同步更新。

## 当前状态

v2 全部落地：经典菜单内联目录树（规则见 `dist\7zipContext.ini`）、压缩/解压走 `7zG.exe`、
WPF 主窗口/设置页/预览弹窗/文件关联/`IPreviewHandler` 全部移除（`7zipContext.Preview` 工程已删）、
AppX 的 Preview 动词已退役（现代菜单 7 个动词）、自检 9 项。

基线：SmokeTest 19/19、ComSmokeTest 59/59、install 自检 9/9。
同名菜单项重复通常来自机器上另一款压缩软件，本项目的经典菜单动词已全部改为自插以避开冲突。
