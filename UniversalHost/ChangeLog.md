# TODO
- 测试通信。
- 实现监控时保存数据，保存为HDF5。
- IAP修改为使用XCP PGM实现。
- 日志显示。
- 远程控制。
- 故障录波。

---

# 修改记录

2026-04-01
- `sln`升级为`slnx`

2026-05-20
- 数据读取协议修改，去除上传间隔。去除超时次数。
- 删除了`SymbolRuntime`中对于端序的判断。
- 修复发送地址时只发送第一个字节的地址的问题。

2025-05-21
- 修改`SymbolRuntime`中的地址生成。现根据PC端序和设备端序，符号大小，起始地址生成相应地址数组。修改`AddressBuffer`为计算属性。
- 升级`Avalonia`及其相关包至`12.0`。
- 使用`Serilog`实现了基础日志记录功能。

2025-05-22
- 将IAP迁移至`Dock.Avalonia`的`Tool`窗口。

2025-05-23
- 优化IAP tool窗口。
- 增加日志开关记录按钮。增加保存至文件开关按钮。
- 实现基本的IapTool窗口布局保存与恢复。需要优化。

2025-05-24
- 优化IapTool的布局保存与恢复。

2025-05-25
- 修复IapTool的Notify不通知问题，在`MainWindowViewModel`新增`ShowNotification()`，在其他VM调用。
- 修复IapTool的`Settings`不更新问题。在`DockableRegistry`中增加更新。
- 修复单独保存`Settings`时删除`layout.json`。

2025-05-26
- 修改`UdpService`类和`ICommService`，提高性能。
- 新增`NotificationService`，增加若干显示通知的方法，修复原`Notification`的内存泄漏问题。
- 修改`IapViewModel`，为`Settings`订阅增加`Dispose`。
- 将`Settings`修改为由`ProjectSaveService`持有的全局单例。其他引用了`Settings`的部分有待修改为使用单例。

2025-05-27
- 修改其他引用了`Settings`的部分至引用全局单例。将`ProjectFilePath`修改为由`SaveService`持有的全局单例。
- 修改`DockFactory`初始化

2025-05-28
- 将`CircularBuffer`换成第三方实现，更加完善
- 实现`MonitoredSymbolsRuntime.VauleString`的定时转换。目前10Hz。
- `SettingWindow`的符号表`DataGrid`高度减少，增加性能。
- 初步实现添加表格监控窗口功能。
- 完善表格监控VM。
- `DockableRegistry`声明为`static`

2025-05-29
- 实现表格监控窗口布局保存与恢复。
- 新建`GlobalStatus`类管理工程开启状态以及监控开启状态。设置清空符号表，清空ELF，删除重复变量等按钮在监控开启时无效。
- 实现部分添加监控变量窗口

2025-05-30
- 完善添加监控变量窗口。
- 修改UDP发送和接收为`async`且零内存分配。
- CRC16改为查表法。
- 修改`SubscribeReadProtocol`及`IapProtocol`的获取发送数组和接收数组解析方法为`Span`，提高性能。
- 修改`CommunicationService`中发送和接收缓存为全局缓存，减少堆内存分配和回收。发送和接收改为异步。
- 完善IAP协议。相关的代码有待修改。

2026-05-31
- 根据新的IAP协议修改协议和通信服务。将IAP流程放到后台线程执行，不阻塞UI。
- 将数据监控的发送和接收放到后台线程进行。
- 根据新的IAP协议修改PS代码。
- 为`GridMonitor`添加右键菜单，可以添加和删除。
- 修复`GridMonitor`列显示不保存和恢复的问题。

2026-06-01
- 删除`SymbolRumtimes`中的`ObservableCollection`，统一使用`SourceList`管理监控变量。
- 实现`GridMonitor`窗口行可拖动改变顺序功能。为拖动增加视觉效果。
- `MonitorSymbolRuntimes`改为`SourceList`提高性能。`Value`转换为`ValueString`放进后台线程。
- 修复跨窗口唤醒`SelectSymbolWindow`时`CheckBox`不更新的问题。
- `GridMonitor`中的`DataGrid`增加右键菜单。
- 符号的创建搜索器移动至符号对应的类下。
- 删除`SymbolInfo`中的`Subscribe`，解决内存泄漏。
- 新增添加监控变量时无法添加重复变量。

2026-06-02
- 新建抽象类`SymbolRuntime`，将`MonitorSymbolRuntime`的存储的值由泛型改为强类型。

2026-06-03
- `UserSymbolInfo`新增`Id`属性，使用`Guid`作为`Id`。`MonitorSymbols`改为`SourceCache`，使用`Id`作为全局唯一标识符。`Runtimes`同步修改为`SouecrCache`。提高性能。
- 增加当变量类型修改时自动重建该变量的`Runtime`。
- `GridMonitor`的顺序改为使用`UserSymbolInfo`的`Id`存储。
- 协议修改为按字读取，优化了协议构建数组和接受解析的效率。修复协议中存在的若干bug。
- CRC16的计算改为slice8。

2026-06-04
- `GlobalStatus`中新增几项用于在通信进行时禁止修改设置。
- 开始切换至XCP协议。

2026-06-06
- 实现了XCP协议STD必须的4个指令。实现了部分通信层服务。

2026-06-07
- 继续实现XCP协议。

2026-06-09
- 实现了连接，标定变量的下载和上传。

2026-06-10
- 实现表格标定窗口及其保存恢复，变量选择等相关功能。实现设置窗口中的标定设置。
- 增加添加监控和标定变量互斥功能。
- 实现DAQ的通信层。
- 将设置窗口中的几个`CheckBox`换成更美观的`ToggleSwitch`。

2026-06-11
- 实现参数文件的保存和恢复
- 开始实现曲线监控窗口。`ScottPlot 5`和`Avalonia 12`暂不兼容，先实现其他功能。

2026-06-12
- 继续实现曲线监控窗口左侧边栏功能。
- 主窗口增加暗亮主题切换按钮。
- 表格标定窗口增加一些通知。
- 继续实现`Dock`功能。

2026-06-13
- 实现多标签页及标签页标题重命名，标签页添加。
- 实现快捷键。仅在`MainWindow`生效。
- 使变量选择窗口的`CheckBox`显示更稳定。在`Toggle`最后增加`SelectedVersion++`。
- 实现保存历史数据至CSV。
- 设置窗口布局变更。串口和UDP设置移动至通信一个标签内。

2026-06-14
- 优化`SymbolRuntime`的`ConvertToDouble`方法，改为0分配。
- `UserSymbolInfo`增加`IsMonitored`属性用于决定是否添加到正在监控的变量列表。
- 设置窗口监控变量列表增加`CheckBox`绑定至该属性。增加全选/全不选`CheckBox`。
- 实现位监控窗口

2026-06-15
- 优化位监控窗口
- 添加 Bit 为 1 时背景变色功能。

2026-06-21
- 由于`XCPlite`只支持动态DAQ，修改`XcpClient`中的DAQ流程为动态DAQ。

2026-06-24
- 开始实现曲线窗口。目前实现了扫描线更新，曲线的添加，删除，切换显示功能。

2026-06-25
- 实现曲线窗口的主题切换。
- 实现`XCP`协议`UserCmd`的设置项与保存恢复。
- 实现`UserCmd`执行窗口和选择窗口。

2026-07-09
- 增加了故障录波的通信，保存，UI按钮。

2026-07-23
- 修复了`XCP Client`释放资源的问题。
- 修复重载ELF文件时标定变量地址不更新的问题。
- `SymbolRuntime`的`StringToValue`方法去掉`try catch`以在输入数据字符串和数据类型不匹配时直接报错。

2026-07-24
- `XcpClient`的心跳包由固定1s间隔发送改为未收到有效数据1s后发送。
- `XcpClient`中初始化的通信超时改为2倍以免与服务类的超时冲突。
- 修复`CalibratedSymbols`的`DataType`改变后UI不变更的问题。