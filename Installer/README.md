# 画面扫描安装程序

开始菜单中的「Soda Terminal · 画面扫描」启动后台助手并打开扫描工作区；助手不显示独立窗口。安装完成默认关闭安装窗口，返回原网页后连接，不再自动打开第二个网页。已运行的助手按成功处理，不把重复启动误报为无法打开。旧的静默协议快捷入口仅在归属完全匹配时迁移；安装失败恢复原入口，用户修改的入口继续保留。

发布入口为 `Soda-Scanner-Setup.exe`（Windows x64），当前安装器 1.2.3、Helper 2.3.20、锁定 1.0.49 Soda fork r37，运行组件 RC8.19。具体下载大小与 SHA256 以网页发行清单和对应 GitHub Release 为准。玩家下载并打开 EXE 后，首次安装自动准备组件并启动画面扫描，无需再点击“开始安装”。窗口展示准备、安装与完成进度，安装事务执行期间不能关闭；失败后可手动重试。已有安装、残留文件或协议/卸载登记时保留修复和卸载入口，不自动覆盖。也可从 Windows“已安装的应用”卸载。当前安装器可一次卸载全部已交付旧版中大小/SHA匹配的程序组件，无需先升级或逐版卸载；卸载保留扫描结果、日志、未知文件和用户替换的文件。

历史版本记录：1.1.0 统一安装包、网页、诊断及升级提示的产品版本，保留 1.0.10 的修复：必需描述文件漏装、图标透明掩码与开始菜单入口，内置 Helper 2.3.12 / r28。识别 1.0.8、1.0.9、1.0.10 后可升级；同版可修复。安装完成默认关闭窗口，开始菜单才主动启动助手并打开工作区。组件缺失显示修复，产品版本与助手内部版本分开。以下为 1.0.9 的安装边界记录：窗口和 EXE 使用 Soda 自有图标，显示工具名与安装版本；识别已发布 1.0.8 的有效同根指针时显示“当前 1.0.8 → 安装 1.0.9”和“升级安装”，同版显示修复，陌生/不完整安装不冒充已知版本。安装沿用事务及被替换程序的恢复副本；失败恢复文件与原指针，注册回退仅覆盖仍符合本次同根身份的入口。卸载使用编译入程序的当前与已发布 1.0.8 文件目录，不读取玩家磁盘上的描述文件决定删除；仅删除大小/SHA 相符的程序、完整归属协议和卸载入口，陌生注册值和程序改写保留。

1.0.8 安装包内置 RC8.8 的完整锁定 ZIP：Helper 2.3.10、锁定 1.0.49 Soda fork（capture r27）、PP-OCRv6 与 Microsoft release VC runtime 14.44.35211.0 的应用本地依赖和许可证。此版优化翻行后的重复确认，修复 A/B 品级边界误判和恢复全屏窗口后的启动顺序；补全失败环节、停止位置及窗口状态，网页反馈保留提交编号并支持一致的重试、复制和下载。沿用现有安装窗口和打包，协议 5 和数据 schema 2 不变。

## 构建

需要 Windows x64 与 .NET 8 SDK。先取得两个精确输入：

- [RC8.19 运行包](https://github.com/Fizz-McBubble/soda-terminal-scanner/releases/download/scanner-runtime-v18.0.0-rc.8.19-local.20261021/soda-scanner-runtime-18-rc8-19-local-20261021-win-x64.zip)
- [应用本地 VC runtime 包](https://github.com/Fizz-McBubble/soda-terminal-scanner/releases/download/scanner-installer-v1.2.3/vc-runtime-14.44.35211-x64.zip)

执行 `./Installer/build.ps1 -RuntimeArchive <ZIP> -VCRuntimeArchive <VC-ZIP> -NsisPath <makensis.exe>`。脚本检查固定输入和编译器 SHA256，不自动下载或运行安装器。NSIS 3.13 的精确来源见 `nsis-toolchain.json`；仅解压官方便携 ZIP，不要求系统安装。中间文件与产物默认写在 `Installer/outputs`。输入、产物与 SDK 依赖不进入源码提交。

1.0.4 使用标准 NSIS 单一 LZMA 固实流（本版使用 96 MB 字典），按“UI → 启动子进程 → 卸载组件 → 运行 ZIP → ready”排序；[ReserveFile](https://nsis.sourceforge.io/Docs/Chapter4.html#4.9.1.8) 提前存放 System 插件，避免启动调用先解压后面的载荷。窗口先显示“正在准备组件”和活动进度条，载荷完整后进入原有安全安装引擎。NSIS 等待子程序退出后清理私有临时目录，完整包由 NSIS CRC 保护；分发仍校验整个 EXE 的大小与 SHA256。

资源路径只由安装 UI 自身 apphost 所在目录及 OS 确认的父进程派生，无生产路径覆盖参数。父进程必须保持存活；ready 标记固定版本和长度，提取失败、父进程退出与两分钟准备超时均显式失败。载荷拒绝链接、错误长度，打开期间禁止写入/删除；锁定 ZIP 的大小/SHA 和编译入元数据的卸载程序大小/SHA 在受管组件写入前校验。离线 ZIP 不进入 managed resource；WinForms 自包含 UI 使用 .NET 单文件压缩，启动仍有解压成本，见 [Microsoft 单文件部署说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)。

## 验证与文件边界

`Installer/tests` 覆盖失败回退、修复、卸载重装、嵌套链接、篡改描述文件、替换文件及登记命令的精确匹配。测试根必须位于 `outputs`，禁止真实用户安装目录；测试模式不写 HKCU、不启动助手或游戏。真实游戏连续扫描与人工安装接受独立验证。

安全渲染入口：`--test-mode --install-root <outputs 内的隔离根> --render-preview <该根内的 PNG>`，不会执行安装。可附 `--preview-state initial|preparing|progress|completion|error|repair`、`--preview-scale 1|1.25|1.5|2` 和 `--preview-details`；状态图是实际控件的渲染测试，不代表真实安装完成。缩放图测试布局，不修改系统 DPI。

只允许默认受管目录的生产安装/卸载。安装串行、失败恢复原指针和组件；修复保留被替换的组件原件。卸载使用编译嵌入的文件大小/哈希清单，不信任磁盘描述文件，不穿过链接，不递归删除安装目录。注册项与进程必须精确匹配本助手路径；移除失败保持可重试。

源码遵守本仓库 MIT 许可。运行包与 VC runtime 保留各自的许可证；VC release DLL 不修改，来源与分发条件见 [Microsoft 可分发文件清单](https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution)。

压缩壳使用未修改的 [NSIS 3.13](https://nsis.sourceforge.io/Download) 与 LZMA 模块，许可及链接例外全文保留在 `NSIS-COPYING.txt`。原作者为 NSIS contributors，不将其视为本项目原创；原始源代码获取位置保留在 `nsis-toolchain.json`。

Installation and repair also create the current-user Start menu entry "Soda Terminal · 画面扫描". It opens the existing protocol service and does not begin scanning. The Unicode InternetShortcut uses the installed Soda uninstaller icon; its URL and icon path identify the exact managed root. Conflicting same-name contents are preserved and installation rolls back. Uninstall removes only the exact owned entry; synthetic tests keep this file inside their isolated root, never the user's Start menu.
