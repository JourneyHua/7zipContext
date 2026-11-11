# 第三方组件声明

本仓库为了「零前置、便携」随包分发了 7-Zip 官方二进制。这些文件**不是本项目的代码**，
不受仓库根目录 `LICENSE`（MIT）约束。

## 7-Zip 26.04 (x64)

| 项 | 内容 |
|---|---|
| 版本 | 7-Zip 26.04 (x64)，构建日期 2026-10-05 |
| 版权 | Copyright (C) 1999-2026 Igor Pavlov |
| 官方网站 | <https://www.7-zip.org/> |
| 官方源码 / 发布 | <https://www.7-zip.org/download.html>、<https://github.com/ip7z/7zip/releases> |
| 许可全文 | [`tools/7-Zip-License.txt`](tools/7-Zip-License.txt)（官网 `license.txt` 原文）、<https://www.7-zip.org/license.txt> |
| 修改情况 | **未修改**，直接取自官方发行包的原始文件 |
| 用途 | 列表/测试 (`7z.exe`)、压缩解压与进度/密码窗 (`7zG.exe`)、文件管理器 (`7zFM.exe`)、编解码库 (`7z.dll`)、中文界面 (`Lang\zh-cn.txt`) |

### 许可要点

- 主体为 **GNU LGPL v2.1 或更高版本**；`7z.dll` 中另含 **BSD 3-clause**（LZFSE / ZSTD 解压）
  与 **BSD 2-clause**（XXH64）部分，以及 **LGPL + unRAR license restriction** 的部分。
- 官方许可明确要求：**以二进制形式再分发时，必须随附该许可信息** —— 这就是
  `tools/7-Zip-License.txt` 存在的原因。`scripts/publish.ps1` 发布时会把它拷到
  `dist\tools\`，并把 `LICENSE`、`THIRD-PARTY-NOTICES.md` 放到 `dist\` 根，
  保证单独分发 `dist\` 时也是合规的。
- **unRAR 限制**：RAR 解压引擎源自 unRAR，其源码不得用于重建 RAR 压缩算法；
  再分发修改版 unRAR 源码（单独或作为其它软件一部分）时必须在文档与源码注释中明确声明。
  本项目未修改、未再分发任何 unRAR 源码。
- LGPL 要求提供对应源码或书面要约：对应版本的完整源码可从上方官方地址获取
  （官网 `download.html` 的 source 包 / `ip7z/7zip` 对应 tag）。

### 随包文件校验（SHA-256，用于核对未被修改）

```
95fff19b73eff77c87c0d7bd32822e43fb90dd2932459f962eb532d8dfb6ba53  7z.exe
363fb45bb3ea1cab98ae5ab4a564c859277ee668e58c08ffd2ecb6fe9afcd534  7zG.exe
58e8d5c2e6cea7589d13790baa5d48a926ac5cbb2858afd19bd309991114a132  7zFM.exe
899cd73d7fc60e1c1c35c7de0f5bed3dc69bfe2cecbd48014d868a347970a0b1  7z.dll
d04e5bd3bf1e3f3754c3603889aa1b659d1dac518c5c6b5c1c49ecf16dca1c01  Lang/zh-cn.txt
```

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
`tools/` 目录内的全部文件，以及上节所述图标素材，均不在 MIT 授权范围内。
