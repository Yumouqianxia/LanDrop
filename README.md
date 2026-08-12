# 局域网搬家

一个面向 Windows 10/11 的局域网文件迁移工具，不依赖 SMB 共享或账户权限。

## 2.2 功能

- 发送端或接收端均可主动发起 TCP 连接。
- `.landrop.part` 断点续传与逐文件 SHA-256 校验。
- 发送端和接收端实时速度、平均速度与总进度。
- 已用时间、预计剩余时间和预计完成时刻。
- 传输过程中添加文件或文件夹，当前批次结束后通过同一连接继续发送。
- GitHub Releases 自动检查、SHA-256 校验、安装并重启。
- 兼容 1.x 单批次握手；追加队列要求两端均为 2.2 或更高版本。

## 网络端口

- UDP `49550`：同网段自动发现。
- TCP `49551`：控制消息与文件传输。

广播发现通常不能跨网段。跨网段时直接填写目标电脑 IP，并在路由器与 Windows 防火墙中仅放行所需方向和端口。

## 构建

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

推送形如 `v2.2.1` 的标签后，GitHub Actions 会生成 `LanDrop.exe`、SHA-256 文件和 Release。程序内更新器读取仓库 `Yumouqianxia/LanDrop` 的最新正式 Release。
