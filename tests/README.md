# 通信回归验证

无需新增测试框架包，使用 .NET 10 控制台运行协议向量和模拟串口会话测试，失败时返回非零退出码：

```powershell
dotnet run --project tests/UniversalHost.Communication.Tests --configuration Debug
```

覆盖普通 COBS 边界、文档 CONNECT 向量、CRC/长度/功能码校验、分块及连续收帧、溢出/残帧重新同步、取消后的候选保留、同步分隔符、串行发送、本机回显、串口会话互斥、IAP 两种写入流程、重试/取消/清理，以及 XCP 命令、DAQ 和接收异常。

物理串口和 RS485 的 RTS 极性、USB 适配器延迟、驱动排空及拔插行为仍需设备联调。
