# 发布验证

此文档提供发布前的验证流程。每次执行应记录源码提交、安装包 SHA256、Windows/.NET/引擎版本，以及预期、实际结果和日志。未执行的项目不得记为通过。

## 自动化检查

```powershell
dotnet build 7zipContext.sln -c Release
dotnet run --project tests/7zipContext.SmokeTest -c Release
dotnet run --project tests/7zipContext.RegressionTest -c Release -- --headless
powershell -NoProfile -ExecutionPolicy Bypass -File tests/deployment-files.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/purge-engine.ps1
python tests/test_icons.py
python scripts/gen_icon.py --check
```

Python 图标测试依赖见 scripts/requirements-icons.txt。无界面测试覆盖隔离文件操作、CLI 和未显示的菜单对象；不代表 Explorer 中的菜单或安装卸载已通过。

## 系统集成检查

在可回退的 Windows 11 测试环境验证：

| 场景 | 检查内容 |
|---|---|
| 构建与打包 | 各工程独立发布后合并，安装包完整、不含引擎二进制并记录哈希 |
| 首次安装与升级 | 用户目录安装、安装自检、INI 保留、默认应用保留及资源管理器恢复 |
| 现代与经典菜单 | 真实 COM 激活、动词执行、图标、预览树及错误降级 |
| 分卷与混选 | 普通包、数字分卷、RAR、传统 ZIP 去重及后续卷排除 |
| 解压与删除 | 取消、密码错误、损坏包和回收站失败时保留源文件，正确返回退出码 |
| 大包与慢设备 | 预览超时或超限可省略，操作项仍可使用 |
| 引擎获取与更新 | 官方校验、离线本机引擎、网络失败、旧版本保留及来源记录 |
| 部署恢复与并发 | 中断、文件占用、复制失败、回滚失败和互斥冲突均可诊断和重试 |
| 默认应用与卸载 | UserChoice 保留；清理仅涉及本项目，未知或本机引擎不删除 |
| 诊断工具 | 实际 COM 成功/失败分支退出正常，多轮运行资源不累积 |

界面检查包括 100%/150%/200% 缩放、浅色/深色/高对比度、双屏 DPI 切换、长路径和大字号。离线场景分别检查运行时或引擎存在与缺失时的结果。

## 已知限制

- 自动化测试不能代替真实 COM、GUI、全新离线安装和卸载验证；目前这些场景的完整验证结果尚未提供。
- 官方引擎下载仍全量读入内存，尚无下载大小上限。
- 主程序、安装器和菜单组件依赖 .NET 8 Desktop Runtime x64；无运行时的离线环境无法直接完成安装。

版本发布说明见 [GitHub Releases](https://github.com/JourneyHua/7zipContext/releases)。
