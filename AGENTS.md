# AGENTS.md

## 项目概况

- 本仓库是基于 Avalonia、ReactiveUI 和 Dock.Avalonia 的 C# 桌面上位机，目标框架为 .NET 10。
- 桌面应用位于 `UniversalHost/UniversalHost.csproj`，独立工程转换器位于 `UniversalHost.ProjectConverter/`，解决方案文件为 `UniversalHost.slnx`。
- 面向用户的说明在根目录 `README.md`；版本号变更记录在根目录 `ChangeLog.md`。详细说明集中在 `docs/`：IAP 协议见 `docs/IAP协议.md`，故障录波格式见 `docs/故障录波结构体定义.md`，快捷键见 `docs/快捷键.md`。修改协议前，先查看对应 Markdown 文档以及相关协议实现。

## 代码结构

- `UniversalHost/Views/`：Avalonia XAML 界面及少量界面代码。
- `UniversalHost/ViewModels/`：ReactiveUI 视图模型；优先沿用现有绑定和 ReactiveUI 源代码生成器的模式。
- `UniversalHost/Services/`：通信、项目保存、符号运行时、日志和通知等服务。
- `UniversalHost/Models/`：项目设置、协议数据结构、符号和运行时模型。
- `UniversalHost.Symbols/`：独立 .NET 10 固件解析类库，不依赖界面或应用设置；`Native/` 为固定 Go 版本的独立读取工具。主机 RID 映射见 `build/SymbolWorker.targets`。
- `UniversalHost.ProjectConverter/`：无界面依赖的独立工程转换器，按相邻格式版本逐步迁移；主程序只做版本检查和异步进程调用。格式契约在 `shared/ProjectMetadata.cs`，程序版本统一在 `build/Versions.props`。
- `tests/`：临时验证程序和固件样本，不加入 Git；验证后删除本次创建的测试程序及产物。
- 新功能应放在职责对应的目录中；避免把协议、业务流程或持久化逻辑塞进视图代码。

## 修改约定

- 保持 `Nullable` 启用，并遵循相邻文件已有的命名、格式和命名空间风格；新增依赖前先确认现有包不能满足需求。
- 界面耗时工作和通信使用异步流程，避免阻塞 UI 线程；遵循现有资源释放、取消和订阅清理方式。
- XCP/IAP 的帧格式、字节序、地址计算、CRC、超时、重试和阶段顺序都属于设备通信契约。修改前检查 `Models/Communication/XcpProtocol.cs`、`Models/Communication/IapProtocol.cs` 及 `Services/Communication/` 中对应实现，不要凭推测改变线上行为。
- 项目文件是 ZIP 容器，当前格式为 2，最低兼容含元数据的格式 1。低版本由独立转换器依赖固件升级，成功后原子替换并备份原文件；扩展方法见 `docs/工程格式兼容.md`。打开当前格式工程只恢复保存内容，不解析固件、不触发保存。
- 仅在版本号变更时更新根目录 `ChangeLog.md`，按版本号记录；不再记录逐日功能开发历史。

## 构建

在仓库根目录执行：

```powershell
dotnet build UniversalHost.slnx --configuration Debug
```

应用构建/发布仅需 .NET 10 SDK，辅助项目统一放在 `dependencies/`，主程序和转换器共用一份 `dependencies/symbol-tools/`；发布的转换器为单文件 EXE。按目标 RID 校验并复制仓库内 `UniversalHost.Symbols/Native/dist/` 的预编译工具，不调用 Go。修改 Go 解析器后，维护者使用固定 Go 1.26.8 执行 `pwsh build/Build-SymbolWorkers.ps1`（可用 `-GoExe` 指定路径）；脚本先运行 Go 测试，再交叉构建六个 RID。源码、二进制、SHA256 和 manifest 必须一起提交。测试程序临时创建于 `tests/`，验证后删除，不纳入解决方案或 Git。

项目启用了 Avalonia 编译绑定，XAML 绑定修改应符合现有编译绑定要求。测试相关文件放在 `tests/` 内。发布时执行 `build/Verify-SymbolPackage.ps1`；跨主机打包 Unix 产物使用 `build/Package-Release.ps1` 保留可执行权限。静态架构检查不能替代对应平台运行验证。
