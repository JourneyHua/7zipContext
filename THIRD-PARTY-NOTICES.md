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
- 若使用 `-EngineSource` 自定义源，无法取得官方 `digest` 时会改验 Windows Authenticode
  数字签名；两者都不过关则**拒绝执行**，不下载来源不明的二进制。

### 我们不会做的事

- 不内置任何第三方镜像或加速源。GitHub 不可达时明确报错，由用户自己用
  `-EngineSource` 指定可达的官方安装包 URL，或用 `-EnginePath` 指向本机已有的 7-Zip 目录。
- 不自动静默升级引擎。更新检查只提示，显式 `--update-engine` 才下载并切换；
  旧版本目录保留，可回滚。

## 商标与图标

- **7-Zip 是 Igor Pavlov / 7-Zip 项目的商标**。本项目是独立的第三方 shell 扩展，
  **不是 7-Zip 官方项目，与其无关联、未获背书或授权**。
- `assets\7zipContext.ico`、`assets\7zipContext.png` 与 `package\` 下的
  `Square44x44Logo.png` / `Square150x150Logo.png` / `StoreLogo.png` 是本项目的菜单图标，
  视觉上沿用了 7-Zip 的图标素材，仅用于指示「此处调用 7-Zip 引擎」。
  图标著作权与商标权归各自权利人所有；如需商业再分发，请自行评估并取得必要授权。
- 本项目不宣称与 7-Zip 有隶属、代理或合作关系。

## MIT 许可的适用范围

根目录 [`LICENSE`](LICENSE) 只覆盖本仓库**原创的源码与文档**
（`src/`、`tests/`、`scripts/`、`package/` 中的代码与清单、构建文件、Markdown 文档）。
运行时下载的 7-Zip 引擎，以及上节所述图标素材，均不在 MIT 授权范围内。
