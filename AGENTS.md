# AGENTS.md

## 项目概况

- 本仓库是基于 Avalonia、ReactiveUI 和 Dock.Avalonia 的 C# 桌面上位机，目标框架为 .NET 10。
- 唯一应用项目位于 `UniversalHost/UniversalHost.csproj`，解决方案文件为 `UniversalHost.slnx`。
- 面向用户的说明在根目录 `README.md`；项目变更记录在 `UniversalHost/ChangeLog.md`。修改协议前，先查看根目录的 `通信协议.xlsx` 以及相关协议实现。

## 代码结构

- `UniversalHost/Views/`：Avalonia XAML 界面及少量界面代码。
- `UniversalHost/ViewModels/`：ReactiveUI 视图模型；优先沿用现有绑定和 ReactiveUI 源代码生成器的模式。
- `UniversalHost/Services/`：通信、项目保存、符号运行时、日志和通知等服务。
- `UniversalHost/Models/`：项目设置、协议数据结构、符号和运行时模型。
- 新功能应放在职责对应的目录中；避免把协议、业务流程或持久化逻辑塞进视图代码。

## 修改约定

- 保持 `Nullable` 启用，并遵循相邻文件已有的命名、格式和命名空间风格；新增依赖前先确认现有包不能满足需求。
- 界面耗时工作和通信使用异步流程，避免阻塞 UI 线程；遵循现有资源释放、取消和订阅清理方式。
- XCP/IAP 的帧格式、字节序、地址计算、CRC、超时、重试和阶段顺序都属于设备通信契约。修改前检查 `Models/XcpProtocol.cs`、`Models/IapProtocol.cs` 及 `Services/Communication/` 中对应实现，不要凭推测改变线上行为。
- 项目文件是 ZIP 容器，设置、Dock 布局和窗口上下文以 JSON 项保存。修改序列化模型时，考虑已有项目文件的兼容性。
- 对用户可见功能或协议行为的重要变更，按 `UniversalHost/ChangeLog.md` 现有日期格式补充记录；纯内部整理可不记。

## 构建

在仓库根目录执行：

```powershell
dotnet build UniversalHost.slnx --configuration Debug
```

项目启用了 Avalonia 编译绑定，XAML 绑定修改应符合现有编译绑定要求。仓库当前没有独立测试项目；若后续新增测试项目，再运行与改动相关的测试。
