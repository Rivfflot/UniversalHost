# UniversalHost

基于 XCP 协议和自定义 IAP 协议的通用上位机工具。

> 项目目前处于早期开发阶段，功能和接口可能会发生变化。

## 项目简介

UniversalHost 用于与支持 XCP 或自定义 IAP 协议的设备进行通信，提供统一的上位机开发基础，便于后续扩展设备连接、数据采集、参数配置和固件升级等功能。

## 主要功能

- 支持 XCP 协议通信
- 支持自定义 IAP 协议
- 提供统一的通信接口
- 支持 UDP 和串口传输；全双工串口支持 XCP/IAP，半双工串口支持 IAP
- 支持设备连接与断开
- 支持参数读取与写入
- 支持数据采集与监控
- 支持固件升级功能扩展

## 开发环境

- .NET 10
- C#

## 快速开始

1. 克隆仓库：

   ```bash
   git clone https://github.com/yourusername/UniversalHost.git
   cd UniversalHost
   ```

2. 运行 `dotnet publish UniversalHost/UniversalHost.csproj -c Release` 编译发布。

   发布时自动将 `docs/使用说明.md` 和 `docs/快捷键.md` 转换为 `使用说明.pdf` 和 `快捷键.pdf`，放在发布目录中，与可执行文件同级（包括单文件发布和 `--no-build` 发布）。构建机器需要 Python 3.10 或更高版本，以及 Microsoft Edge 或 Chrome；无需安装 Python 第三方包。发布后的程序不需要 Python 或浏览器。

   默认通过 `PATH` 中的 `python` 启动转换程序，自动查找浏览器。如需指定路径，可传入 `-p:ShortcutsPdfPython="C:/Python/python.exe"` 和 `-p:ShortcutsPdfBrowser="C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"`，这两个设置同时适用于两份文档。找不到浏览器时提示警告并跳过 PDF 生成，发布继续；其他转换错误仍会使发布失败。

   也可以单独生成：

   ```powershell
   python tools/publish_shortcuts.py docs/快捷键.md UniversalHost/bin/Publish/快捷键.pdf
   python tools/publish_shortcuts.py docs/使用说明.md UniversalHost/bin/Publish/使用说明.pdf
   ```
   
3. 运行。


串口固件升级前须先断开 XCP 设备连接，升级及清理期间不能重新连接；可用“取消”结束升级会话。接收超时作为设备处理与调度裕量，程序另计线上传输时间。设备端须实现 [传输层约定.md](docs/传输层约定.md) 中的 COBS 封装。串口 DAQ 采样率由设备设定，应按全部 ODT 线上字节数预留带宽裕量，启动监控时日志会给出当前布局的带宽估算。

## TODO

- 实现监控时保存数据，保存为HDF5。
- IAP 增加目标设备固件合法性检查，避免选择错误 BIN。
- 日志显示。
- 远程控制。

项目目录结构会随着功能开发持续调整。

## 协议说明

### XCP

XCP（Universal Measurement and Calibration Protocol）是一种用于测量、标定和控制的标准协议。项目将根据实际通信传输层逐步实现相关功能。

### 自定义 IAP

自定义 IAP 协议用于设备固件升级及相关控制操作，具体数据格式和命令定义以项目实现及设备协议文档为准。

面向操作人员的完整使用流程见 [使用说明.md](docs/使用说明.md)，键鼠操作见 [快捷键.md](docs/快捷键.md)。协议说明也集中在 `docs/`：UDP 与串口通信规则见 [传输层约定.md](docs/传输层约定.md)，IAP 定义见 [IAP协议.md](docs/IAP协议.md)，故障录波格式见 [故障录波结构体定义.md](docs/故障录波结构体定义.md)。

## 许可证

采用 MIT 许可证。
