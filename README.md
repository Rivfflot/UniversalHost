# UniversalHost

基于 XCP 协议和自定义 IAP 协议的通用上位机工具。

> 项目目前处于早期开发阶段，功能和接口可能会发生变化。

## 项目简介

UniversalHost 用于与支持 XCP 或自定义 IAP 协议的设备进行通信，提供统一的上位机开发基础，便于后续扩展设备连接、数据采集、参数配置和固件升级等功能。

## 主要功能

- 支持 XCP 协议通信
- 支持自定义 IAP 协议
- 提供统一的通信接口
- 支持设备连接与断开
- 支持参数读取与写入
- 支持数据采集与监控
- 支持固件升级功能扩展

## 开发环境

- Windows
- Visual Studio 2026
- .NET 10
- C#

## 快速开始

1. 克隆仓库：

   ```bash
   git clone https://github.com/yourusername/UniversalHost.git
   cd UniversalHost
   ```

2. 使用 Visual Studio 打开解决方案：

   找到并打开 `UniversalHost.sln` 文件。

3. 还原依赖并编译项目：

   在 Visual Studio 中，右键单击解决方案，选择“还原 NuGet 包”，然后选择“生成解决方案”。

4. 运行项目：

   按下 F5 键或者点击“启动”按钮运行项目。

## TODO

- 实现监控时保存数据，保存为HDF5。
- IAP修改为使用XCP PGM实现。
- 日志显示。
- 远程控制。

项目目录结构会随着功能开发持续调整。

## 协议说明

### XCP

XCP（Universal Measurement and Calibration Protocol）是一种用于测量、标定和控制的标准协议。项目将根据实际通信传输层逐步实现相关功能。

### 自定义 IAP

自定义 IAP 协议用于设备固件升级及相关控制操作，具体数据格式和命令定义以项目实现及设备协议文档为准。

## 许可证

采用 MIT 许可证。