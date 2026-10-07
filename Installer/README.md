# 扫描助手安装程序

发布入口为 `Soda-Scanner-Setup.exe`（Windows x64）。打开后安装并启动扫描助手；再次打开可修复或卸载，也可从 Windows“已安装的应用”卸载。卸载保留扫描结果、日志、未知文件和用户替换的文件。

安装包内置已发布 RC8.3 的完整锁定 ZIP，并单独补齐 Microsoft release VC runtime 14.44.35211.0 的应用本地依赖与许可证。Helper 2.3.6、锁定 1.0.49 Soda fork 和 OCR/模型的原有字节保持不变。未发布的可靠性候选未进入本安装发行。

## 构建

需要 Windows x64 与 .NET 8 SDK。先取得两个精确输入：

- [RC8.3 运行包](https://github.com/Fizz-McBubble/soda-terminal-scanner/releases/download/scanner-runtime-v18.0.0-rc.8.3/soda-scanner-runtime-18-rc8-3-win-x64.zip)
- [应用本地 VC runtime 包](https://github.com/Fizz-McBubble/soda-terminal-scanner/releases/download/scanner-installer-v1.0.0/vc-runtime-14.44.35211-x64.zip)

执行 `./Installer/build.ps1 -RuntimeArchive <ZIP> -VCRuntimeArchive <VC-ZIP>`。脚本检查固定大小及 SHA256，不自动下载或运行安装器。中间文件与产物默认写在 `Installer/outputs`。输入、产物与 SDK 依赖不进入源码提交。

## 验证与文件边界

`Installer/tests` 覆盖失败回退、修复、卸载重装、嵌套链接、篡改描述文件、替换文件及登记命令的精确匹配。测试根必须位于 `outputs`，禁止真实用户安装目录；测试模式不写 HKCU、不启动助手或游戏。真实游戏连续扫描与人工安装接受独立验证。

只允许默认受管目录的生产安装/卸载。安装串行、失败恢复原指针和组件；修复保留被替换的组件原件。卸载使用编译嵌入的文件大小/哈希清单，不信任磁盘描述文件，不穿过链接，不递归删除安装目录。注册项与进程必须精确匹配本助手路径；移除失败保持可重试。

源码遵守本仓库 MIT 许可。运行包与 VC runtime 保留各自的许可证；VC release DLL 不修改，来源与分发条件见 [Microsoft 可分发文件清单](https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution)。
