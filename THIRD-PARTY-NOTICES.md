# 第三方组件声明

7zipContext **不随仓库、也不随发布产物携带任何 7-Zip 可执行文件**。
压缩/解压能力由安装阶段按需获取的本机 7-Zip 提供：优先复用机器上已有的安装，
没有才从官方发布页下载。源码仓库里只有本项目的原创代码。

## 7-Zip（运行时依赖）

| 项 | 内容 |
|---|---|
| 版权 | Copyright (C) 1999-2026 Igor Pavlov |
| 官方网站 | <https://www.7-zip.org/> |
| 许可全文 | <https://www.7-zip.org/license.txt> |
| 发布与源码 | <https://github.com/ip7z/7zip/releases>、<https://www.7-zip.org/download.html> |
| 获取方式 | 安装阶段由 `scripts/install.ps1` 调用 `7zipContext.exe --ensure-engine` 完成 |
| 落地位置 | `%LOCALAPPDATA%\7zipContext\engine\{版本}\`（用户目录，不写 Program Files、不需管理员） |
| 校验 | 下载前从 GitHub Release API 读取该资产的 `digest`（SHA-256），下载后比对，不符即丢弃 |

### 许可要点

- 主体为 **GNU LGPL v2.1 或更高版本**；`7z.dll` 中另含 **BSD 3-clause**（LZFSE / ZSTD 解压）
  与 **BSD 2-clause**（XXH64）部分，以及 **LGPL + unRAR license restriction** 的部分。
- **unRAR 限制**：RAR 解压引擎源自 unRAR，其源码不得用于重建 RAR 压缩算法；
  再分发修改版 unRAR 源码时必须在文档与源码注释中明确声明。本项目未修改、未再分发任何
  unRAR 源码，也不重新分发 7-Zip 二进制。
- 官方许可要求「以二进制形式再分发时随附许可信息」。本工具下载的是**未经修改的官方安装包**，
  解包时会把其中的 `License.txt` 一并保留在引擎目录中，因此用户机器上的副本始终自带许可全文。
  需要查看时也可直接访问上方官方许可链接。
- LGPL 要求提供对应源码或书面要约：对应版本的完整源码可从上方官方地址获取
  （官网 `download.html` 的 source 包 / `ip7z/7zip` 对应 tag）。
- 自定义下载源已停用；仅采用官方 Release API 带 SHA256 digest 的安装器资产，不再回退 Authenticode 校验。

### 我们不会做的事

- 不内置任何第三方镜像或加速源。GitHub 不可达时明确报错，由用户自己用
  `-EnginePath` 指向本机已有的 7-Zip 目录进行离线安装。
- 不自动静默升级引擎。更新检查只提示，显式 `--update-engine` 才下载并切换；
  旧版本目录保留，可回滚。

引擎落地前会检查四件套与官方安装包中的 `License.txt`；缺失即拒绝本次安装。
重复获取同一版本时写入新的版本副本目录，校验与复制完成后才切换指针，保留原有引擎用于回滚。
引擎变更使用跨进程互斥；目录对应的来源与校验记录先保存，再切换 `engine.path`。
`engine-records` 中的记录只描述运行时获取的依赖，不包含或分发第三方二进制。

命令行通过统一退出码报告引擎结果：引擎可用但关联刷新有警告返回部分成功，联网检查未完成返回失败；不影响现有引擎的继续使用。非交互压缩/解压调用控制台引擎，不打开进度窗或等待密码输入。

## 商标与图标

- **7-Zip 是 Igor Pavlov / 7-Zip 项目的商标**。本项目是独立的第三方 shell 扩展，
  **不是 7-Zip 官方项目，与其无关联、未获背书或授权**。
- `assets\7zipContext.ico`、`assets\7zipContext.png` 与 `package\` 下的
  `Square44x44Logo.png` / `Square150x150Logo.png` / `StoreLogo.png` 是本项目的菜单图标，
  使用项目现有的蓝色拉链图标，仅用于指示「此处调用 7-Zip 引擎」。
  图标著作权与商标权归各自权利人所有；如需商业再分发，请自行评估并取得必要授权。
- 本项目不宣称与 7-Zip 有隶属、代理或合作关系。

## MIT 许可的适用范围

根目录 [`LICENSE`](LICENSE) 只覆盖本仓库**原创的源码与文档**
（`src/`、`tests/`、`scripts/`、`package/` 中的代码与清单、构建文件、Markdown 文档）。
运行时下载的 7-Zip 引擎，以及上节所述图标素材，均不在 MIT 授权范围内。

2026-11-12 更新检查策略：使用 update-check.json 原子保存尝试时间、成功时间、连续失败次数、下次请求时间和历史结论，绑定当前引擎规范目录与版本。成功缓存 24 小时；失败等待 15、30、60、120、240 分钟，随后最多 6 小时；--force 跳过等待。请求前先记录尝试，未完成请求暂缓 5 分钟；无法记录则不请求。失败不清除同一引擎的历史成功结论，但明确标为历史结果，CLI 仍返回 1。结果展示尝试/成功/下次请求 UTC 时间；旧 last-update-check、last-update-result.json 不再作为有效缓存，因其缺少引擎身份，首次按新策略重查。此变更不改变下载来源、校验或许可。
2026-11-12 卸载审查决定：-Purge/-PurgeEngine 删除失败必须报告路径并返回非零退出码，不得提示全部完成；注册残留时暂不删除程序及引擎，保留重试条件。引擎逐目录核验 engine-records 的路径绑定来源（兼容匹配的旧 engine.json），仅删除确认 downloaded 的目录；本机、未知或冲突来源保留并列出，不清空整个 engine 根目录。删除目录成功后才清理对应来源记录；仅当当前指针目标已删除，才清理指针及相关查询状态。锁失败、重解析路径及目录越界均拒绝删除。测试命令：powershell -NoProfile -ExecutionPolicy Bypass -File tests\purge-engine.ps1，仅操作自行创建的临时目录，不执行真实卸载。
2026-11-12：图标源归档为assets/icon-source.ico（复制现有ICO，保留所有尺寸），统一脚本导出程序与AppX资源。固定Pillow为开发时图像导出依赖，不随运行时应用分发；本次未下载或引入新的图标素材，不变更原素材权利。
