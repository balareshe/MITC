# PCL MITC

**PCL MITC 是一个基于 [Plain Craft Launcher 2](https://github.com/Meloong-Git/PCL) 2.13.0.1 的第三方修改版启动器。**

本仓库是**非官方**的第三方二次创作产物，与龙腾猫跃及 PCL 官方没有任何隶属、授权或认可关系。

---

## 这是什么

- 基线：PCL 2.13.0.1（commit `5b05f87`）
- 形态：VB.NET / WPF / .NET Framework 4.8 的桌面启动器
- 命名：按《PCL 存储库合理使用指南》重度使用第 2 条，名称以 `PCL` 开头，`MITC` 为后缀标识

## 相对上游做了什么

| 方面 | 说明 |
|---|---|
| 更新通道 | 自建 OTA 服务端（版本、公告、联机节点、联机模块），全部签名校验 |
| 联机 | 恢复上游开源版被移除的 EasyTier 联机流程，改用自建中继与房间名前缀 |
| 授权 | 增加授权码校验（人工审核发放），用于控制分发范围 |
| 界面 | 移除启动动画与部分个性化开关，标题栏字标重绘 |
| 品牌 | 全量改名为 PCL MITC（数据目录、授权码前缀等内部标识保持不变） |

下载与授权申请：<https://pcl.umrca.com/>

> 使用本版本需要一个经人工审核发放的授权码，不是下载即用。

## 许可证与合规

本仓库保留上游的 [`LICENCE`](LICENCE)（《PCL 分发有限许可》与《PCL 存储库合理使用指南》），并按该指南的重度使用条款执行：

1. 明确声明这是第三方基于 PCL 的独立二次创作产物；
2. 名称以 `Plain Craft Launcher (PCL)` 开头并带第三方修改后缀；
3. 关于页首位给出龙腾猫跃的署名与赞助链接，公开源代码，继续使用原许可文件；
4. 保留与 `McLaunchPrecheck` 一致的 Minecraft 正版购买劝导；
5. 不提供与赞助解锁类似的功能（界面主要色相保持蓝色）。

除本许可明确授予的权利外，相关知识产权仍归原开发者所有。

## 关于上游

本仓库不是 PCL 官方源码仓库。上游项目地址：

- 源代码：<https://github.com/Meloong-Git/PCL>
- 官方网站：<https://meloong.com/PCL>
- 帮助文档库：<https://github.com/LTCatt/PCL2Help>

## 鸣谢

- 龙腾猫跃：Plain Craft Launcher 的作者
- bangbang93：BMCLAPI 镜像源与 Forge 安装支持
- 以及上游 `LICENCE` 与关于页中列出的全部第三方项目

完整鸣谢见启动器内「更多 → 关于与鸣谢」。
