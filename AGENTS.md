# 开发约定

7zipContext 是 Windows 11 的 7-Zip 菜单扩展。产品行为和公共接口以 [SPEC-v2.md](SPEC-v2.md) 为准。

## 构建与验证

```powershell
dotnet build 7zipContext.sln -c Release
dotnet run --project tests/7zipContext.SmokeTest -c Release
dotnet run --project tests/7zipContext.RegressionTest -c Release -- --headless
```

菜单或注册变更还需在独立环境执行真实 COM 和安装验证，流程见 [发布验证](docs/RELEASE-VALIDATION.md)。构建和无界面测试不能代替系统集成验证。

## 实现约束

- 现代菜单使用 comhost 与 SurrogateServer；经典菜单在 Explorer 进程内运行，异常不得越过 COM 边界。菜单读取遵守时间和规模预算，不联网获取引擎。
- 在 dllhost 中从 Assembly.Location 定位应用，不依赖 AppContext.BaseDirectory。comhost 映射变更后完整重新发布；各工程独立输出后合并。
- dist 是已注册的 AppX 根目录，不得整体删除或重命名。部署先准备、校验和备份，再停止占用宿主并切换文件；COM 自检放在独立子进程。
- 图形化安装器复用注册脚本。部署先取部署租约，再取引擎锁；失败保留恢复材料，分别报告文件与注册状态。
- 安装不覆盖默认应用或删除 UserChoice。旧版本注册名称保留必要字面量，以便清理；不得修改其他程序的菜单。
- PowerShell 脚本使用 UTF-8 BOM，纯 ASCII 的 7zipContext-menu.ps1 除外。含星号的注册表键使用 LiteralPath 或 Registry API。
- 配置读写和默认模板统一使用 Core.SettingsDocument；保留未知键、注释和显式空列表。分卷入口与安全删除规则必须分别验证。
- CLI 退出码、非交互模式和菜单编号是公共契约。隐藏命令不得意外弹窗；部分成功不得返回全部成功。
- 图标只编辑 assets/icon-source.ico，通过 scripts/gen_icon.py 导出；原生菜单、位图和 COM 资源按作用域释放。
- 测试工程保持在解决方案中；测试结果应记录对应源码或发布包，不能沿用旧产物结果。

## 公开内容

- README 只保留产品介绍与使用说明；版本更新记录放在 GitHub Releases。
- 公开文档、注释和提交说明不包含聊天记录、逐项审批过程、会话约束、本机用户名路径、代理地址或凭据。
- 内部工作笔记不进入版本控制。第三方来源、许可和商标说明保留在 THIRD-PARTY-NOTICES.md。
- 仓库和安装包不携带 7-Zip 二进制；引擎仅从官方验证资产或本机离线目录获取。
