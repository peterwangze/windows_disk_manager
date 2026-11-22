# Windows 磁盘空间管理器

Windows 磁盘空间管理器用一棵可展开的文件树展示每个磁盘、目录和文件的占用。每一层按占用空间从大到小排列，横条表示该项目占父目录的比例。

## 下载和启动

在 [GitHub Releases](https://github.com/peterwangze/windows_disk_manager/releases) 下载 Windows x64 压缩包，解压后运行 `WindowsDiskManager.exe`。请将 `DiskScanner.exe` 与主程序放在同一目录。适用于安装了 .NET Framework 4.8 的 64 位 Windows 10 / 11，发布包已包含扫描器所需的 Python 运行时。

要固定安装，可在解压目录打开 PowerShell 并运行下列命令。安装程序会复制文件到当前用户的应用目录，并创建名为「磁盘空间管理器」的开始菜单入口。

```powershell
.\Install.ps1
```

## 浏览、刷新和删除

- 首次启动会扫描本机磁盘。以后启动时会直接显示上次完成的快照。点击「刷新」或按 **F5** 可重新扫描，扫描过程中可以浏览旧快照。
- 展开目录可查看文件与子目录的占用，鼠标悬停在大小上可查看精确字节数。每行的「打开」或「定位」按钮可在资源管理器中打开目录或选中文件。
- 勾选文件或目录后，点击「移入回收站」，在确认窗口核对完整路径。选择目录会包含其中的内容，重复勾选的子项只处理一次。「查看处理结果」会列出每一项的结果。成功移入回收站的内容可从 Windows 回收站还原，清空回收站后相应空间才会释放。
- 快照生成后发生变化的项目会在删除前重新核对身份。磁盘根目录、管理器自身使用的文件和无法确认身份的项目受保护。

## 统计口径

「占用空间」是文件的已分配字节数，「文件大小」是逻辑长度，两者在压缩、稀疏和小文件上可能不同。目录按照可见文件路径累加，硬链接的多条路径可能指向同一份数据。选择磁盘根节点可以查看按文件标识去重后的已统计占用。磁盘卡片显示 Windows 报告的整盘使用量，其中包括文件系统开销和无法读取的内容。

目录重解析点会作为节点显示并跳过递归遍历。读取受限和占用未知的项目会在树中标记。扫描在一段时间内完成，期间其他程序写入或移动文件可能影响结果。

快照保存在当前用户的 `%LOCALAPPDATA%\WindowsDiskManager`。程序在本机扫描和处理文件。仓库和发布包均不包含个人磁盘快照。

## 从源码构建

在 64 位 Windows 上安装 64 位 Python 和 .NET Framework 4.8。本项目使用 Windows 上的 Framework C# 编译器和系统 SQLite，扫描器使用 Python 标准库。下列命令会构建桌面程序、打包独立扫描器并生成发行压缩包。

```powershell
python -m pip install PyInstaller==6.19.0
.\build.ps1
.\dist\WindowsDiskManager\WindowsDiskManager.exe
```

构建脚本支持 `-Python <解释器路径>`、`-Part Viewer` 和 `-Part Scanner`。完整构建的 ZIP 位于 `dist`。

## 隔离验证

`WDM_DATA_DIR` 可为单次运行指定独立的快照目录，`WDM_SCAN_ROOT` 可将扫描范围限制到指定测试目录。使用这两个变量，可在临时目录内验证刷新、文件勾选和移入回收站的完整流程。

完成构建后，可运行下面的回归测试。界面测试会打开临时实例，在生成的测试目录中操作文件，并从回收站还原测试文件。

```powershell
$env:WDM_SCANNER_EXE = (Resolve-Path '.\dist\WindowsDiskManager\DiskScanner.exe').Path
python -B -m unittest discover -s tests -p test_scan.py -v
powershell.exe -NoProfile -STA -File .\tests\recycle.ps1
powershell.exe -NoProfile -STA -File .\tests\ui-smoke.ps1
```
