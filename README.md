# 7zipContext

给 7-Zip 补一套干净的 Windows 11 右键菜单。

**这不是压缩软件，而是 7-Zip 的 shell 增强层。** 压缩、解压、加密、分卷、进度条、密码框
全部由随包的 7-Zip 组件（`tools\7z.exe` / `7zG.exe` / `7zFM.exe`）完成；7zipContext 只做一件事：
把右键菜单做干净 —— Win11 紧凑菜单用 AppX `IExplorerCommand`，经典菜单用 `IContextMenu` 内联目录树。

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
`dist\7zipContext.ini` 里调整。

两套菜单各管各的：

- **Win11 现代菜单**（右键第一层）：7 个动词，没有预览
- **经典菜单**（右键 → 显示更多选项 / `Shift+F10`）：上面那 7 个 + 内联预览树

进度条和密码框全部由 `7zG.exe` 提供（7-Zip 自己的窗口），7zipContext 不画任何 UI。

## 安装

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -RestartExplorer
```

脚本会发布到 `dist\`、注册两套菜单，并**自动自检 9 项**：dist 产物、AppX 菜单包、
预览窗格残留为 0、**默认打开方式已注册（7zFM 3/3）**、菜单动词 COM 激活、
经典菜单 handler 注册（4/4）、handler 实例化、**菜单命令可执行（真跑一次解压）**、旧动词清零。

需要 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（自检会提示）。

## 卸载

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
```

清理不依赖 `dist\`（换机残留同样卸得干净），卸完自检 0 残留。可选参数：`-Purge` 顺带删 `dist\`。

## 默认打开方式

`.zip` / `.7z` / `.rar` 交给 `tools\7zFM.exe`（7-Zip 文件管理器，含中文界面）。

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

- **7zipContext.Core** — 7z 后端（list/extract/compress/test）、智能解压规则、回收站删除
- **7zipContext.App** — 纯 CLI 执行体（无窗口），右键菜单唯一入口
- **7zipContext.Shell** — Win11 `IExplorerCommand` 动词的 comhost 类库（7 个），由 dllhost 承载
- **7zipContext.Classic** — 经典菜单 `IContextMenu` handler，**跑在 explorer.exe 进程内**，负责内联目录树

v1 的 WPF 主窗口、设置页、预览弹窗、文件关联、`IPreviewHandler` 预览窗格**已全部移除**；
`install.ps1` 仍会清理老机器上的这些残留。

```
src/7zipContext.Core     逻辑与后端        src/7zipContext.Shell    现代菜单动词 comhost
src/7zipContext.App      CLI（7zipContext.exe）   src/7zipContext.Classic  经典菜单 handler
package/          AppX 清单 + logo
scripts/          install / uninstall / publish / 7zipContext-menu
tests/            SmokeTest（Core 19）/ ComSmokeTest（动词 59）/ ComProbe / UiTest
assets/           7zipContext.ico、7zipContext.png
tools/            7z.exe + 7zG.exe + 7zFM.exe + 7z.dll + Lang（7-Zip 26.04）
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

## 排障

- **右键点了没反应**：看 `%TEMP%\7zipContext-invoke-error.log`；`install.ps1` 的「菜单命令可执行」就是测这条。
- **菜单里没有 7zipContext**：Win11 紧凑菜单只显示 AppX 动词；经典 handler 要「显示更多选项」。
- **预览树是空的/只有一行灰字**：`%TEMP%\7zipContext-classic.log` 里有 `status=Timeout/Encrypted/Error`；
  超时说明包太大（250ms 硬超时），这是设计行为。
- **预览树位置或层级不对**：改 `dist\7zipContext.ini`，不用重装。
- **同名菜单项出现两次**：多半是机器上还装着另一款带右键菜单的压缩软件，它也注册了同名项
  （owner-draw 项，文本取不到）；关掉它的菜单集成即可。
- **装完菜单没变**：重启资源管理器；打包 COM 的菜单有缓存。
- **重装报 `0x80073D02`**：`dllhost` 还加载着 dist 里的 comhost，`install.ps1` 会清理并自动重试。
- **改完 `AppxManifest.xml` 必须重装**。

## 许可与致谢

- 本项目自身代码采用 [MIT](LICENSE)。
- `tools\` 下是**未经修改的 7-Zip 26.04 (x64) 二进制**，版权 © 1999-2026 Igor Pavlov，
  主要许可为 LGPL v2.1+，另含 BSD 2/3-clause 与 unRAR 许可限制。
  完整许可文本见 [`tools\7-Zip-License.txt`](tools/7-Zip-License.txt)，
  源码获取方式与逐项说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

**关系声明**：7zipContext 是独立的第三方 shell 扩展，不是 7-Zip 官方项目，与 Igor Pavlov
及 7-Zip 项目无关联、未获其背书。「7-Zip」及其图标归各自权利人所有，本仓库使用它们仅为
标明所调用的压缩引擎。
