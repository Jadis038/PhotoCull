# PhotoCull（影·筛）macOS 版 —— 规划中

macOS 版尚未开始开发，完整技术方案与移植清单见 [docs/MAC移植提示词.md](../docs/MAC移植提示词.md)。

- UI：Avalonia UI 11 + .NET 10 LTS（universal2：arm64 + x64）
- 兼容目标：macOS 16、26、27（LSMinimumSystemVersion=11.0 兜底）
- 复用 Windows 版 Core 引擎（扫描/解码/缓存/队列/标记/索引），替换平台相关层
- LibRaw：brew 安装或自编译 dylib
- 打包：.app + .dmg（含 Gatekeeper 说明）

欢迎参与：认领移植请开 Issue。
