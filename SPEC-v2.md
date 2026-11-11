# 7zipContext v2 规格（冻结于 2026-11-10，2026-11-11 校订）

> 本文件替代 v1 的「压缩软件」定位。v1 的 README/AGENTS 描述的是已作废的形态。

## 一、定位

**不是压缩软件，而是「7-Zip 引擎 + 自建右键菜单」的 shell 增强层。**

压缩/解压/加密/分卷全部交给随包的 `tools\7z.exe` / `7zG.exe`；7zipContext 只负责右键体验。
主窗口、设置页、文件关联、双击打开——全部不做。

## 二、冻结决策

| # | 决策 | 理由 |
|---|------|------|
| 1 | 引擎自带 `tools\7z.exe`，不依赖系统装的 7-Zip | 便携，零前置 |
| 2 | 进度与密码框交给 `7zG.exe` | 零 UI 代码 |
| 3 | 压缩调 `7zG.exe`，参数固定、不加密 | 无设置页 |
| 4 | 删 WPF 主窗口 / 设置页 / 文件关联 | 定位收窄 |
| 5 | 删 `PreviewWindow`（弹窗预览） | 改为菜单内联树 |
| 6 | 预览 = **经典菜单内联目录树** | 不开窗即可看结构，菜单内层级直观 |
| 7 | 现代菜单（AppX `IExplorerCommand`）与经典菜单 handler **两套并行** | 内联树只能经典 |
| 8 | 开关不设 UI，安装参数定死（`-Menu modern\|classic`） | 无设置页 |
| 9 | 不接管其它软件的菜单；自己的动词全部自插 | 避免重复项与归属不明 |
| 10 | 文案使用自有措辞，不引用任何第三方产品名 | 公开仓库的商标与合规要求 |

## 三、保留 / 删除

保留：
- `7zipContext.Core`——7z 封装、智能解压规则、回收站删除（唯一真正的资产）
- `7zipContext.Shell`——现代菜单 comhost（7 个动词，Preview 已退役）
- `7zipContext.Classic`——托管 `IContextMenu` handler，负责「压缩文件预览 + 内联目录树」
- `7zipContext.App/PasswordDialog.cs`——仅作 CLI 在 `7zG.exe` 不可用时的兜底，主路径不经过它

删除（2026-11-11 全部完成）：
- `7zipContext.Preview` **整个工程**（`IPreviewHandler` 预览窗格）
- `7zipContext.App/MainWindow.cs`、`SettingsWindow.cs`、`PreviewWindow.cs`
- 文件关联注册（v2 不注册；`install.ps1` 仍负责清理旧残留）
- 预览窗格 `ShellEx\{8895b1c6}` 注册（**清理逻辑保留在 `ShellIntegration` 与 `uninstall.ps1`**）

新增：
- `7zipContext.Classic`——托管 `IContextMenu` handler，负责「压缩文件预览 + 内联目录树」

## 四、硬约束（实测）

| 项 | 数字 | 来源 |
|---|---|---|
| `7z.exe` 进程启动 | 120–135 ms | `tools\7z.exe` 空跑 x3 |
| 列表边际成本 | ≈ 8 µs / 条目 | 14,653 条目 235 ms |
| 典型包（<1k 条目） | ~125 ms | — |
| 1.5 万条目 | 235 ms | JDK src.zip |

**规则：**
1. `.zip` 优先用 `ZipArchive` 读中央目录（毫秒级），**不要起进程**。
2. 其他格式才起 `7z.exe`，**硬超时 250 ms**。
3. 超时 / 损坏 / 加密 / 空包 → 降级成一行灰色不可点文字，**绝不阻塞菜单**。
4. 默认渲染规则（`dist\7zipContext.ini` 可覆盖）：根目录文件夹 + 文件全部显示、文件夹在前、
   同类按名称升序；子层只展开文件夹、每个文件夹最多前 3 个、子项紧跟自己的父目录；
   `rootMax=20`、`childrenPerFolder=3`、`showFilesInSubfolders=0`、`maxDepth=1`。
   根目录超过 `rootMax` 时追加一行「…另有 N 项」汇总。

## 五、已收敛 / 遗留

1. **内联树归属已定案**：由 `7zipContext.Classic` 的 `IContextMenu` 渲染。早期排查时机器上
   另一款压缩软件也注册了同名的右键预览项，导致截图里的树难以区分；现已通过「动词自插」把
   本项目的一组菜单连成一块，不再依赖第三方菜单。同名重复项由用户自行关闭对方的菜单集成。
2. **不隐藏其它软件的菜单**：Windows 没有稳定、可逆的公开接口去做这件事，冻结为「不接管」。
3. **`7zG.exe`、`7zFM.exe`、`Lang\zh-cn.txt` 已补入 `tools\`**，`publish.ps1` 会一并拷贝。
4. **经典菜单重复项已消除**：清单 `*` 组只留解压 4 条 + 快速压缩，「添加到」两条改由 handler 自插。
5. 遗留：内联树是 owner-draw 之外的普通文本项，层级缩进靠图标位图的左侧留白实现，
   换主题/高 DPI 下的对齐尚未全覆盖测试。

## 六、阶段

1. **spike**：最小托管 `IContextMenu`，只加一条 ownerdraw 项，验证 explorer 加载不崩、
   能拉起 `7zipContext.exe`。（最高风险，先做）
2. 内联树：接 `7zipContext.Core` 列表 + 超时降级。
3. 删代码：WPF 四件套、文件关联、IPreviewHandler。
4. 进度/密码切 `7zG.exe`。
5. 自检改造（7 项 → 新基线）。
