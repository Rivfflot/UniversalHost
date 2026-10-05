using System;
using System.IO.Ports;
using UniversalHost.Models;

namespace UniversalHost.Services.Communication.Serial;

/// <summary>会话启动时的串口参数快照，同时负责线上时间预算。</summary>
public sealed class SerialConnectionOptions
{
    public string PortName { get; }
    public int BaudRate { get; }
    public Parity Parity { get; }
    public StopBits StopBits { get; }
    public SerialDuplexMode DuplexMode { get; }
    public SerialDirectionControl DirectionControl { get; }
    public int TurnaroundDelayMilliseconds { get; }
    public bool HasLocalEcho { get; }
    public int TimeoutMilliseconds { get; }
    public double BitsPerCharacter => 1 + 8 + (Parity == Parity.None ? 0 : 1) +
        (StopBits == StopBits.OnePointFive ? 1.5 : StopBits == StopBits.Two ? 2 : 1);

    public SerialConnectionOptions(SerialConfig config)
    {
        PortName = config.SelectedSerialPort.Trim();
        BaudRate = config.BaudRate;
        Parity = config.ParitySetting;
        StopBits = config.StopBitsSetting;
        DuplexMode = config.DuplexMode;
        DirectionControl = config.DirectionControl;
        TurnaroundDelayMilliseconds = config.TurnaroundDelayMilliseconds;
        HasLocalEcho = config.HasLocalEcho;
        TimeoutMilliseconds = config.TimeoutMilliseconds;
        if (string.IsNullOrWhiteSpace(PortName))
            throw new InvalidOperationException("请先选择串口");
        if (BaudRate <= 0 || TimeoutMilliseconds <= 0)
            throw new InvalidOperationException("串口波特率和接收超时必须大于 0");
        if (!Enum.IsDefined(Parity) || !Enum.IsDefined(StopBits) || StopBits == StopBits.None ||
            !Enum.IsDefined(DuplexMode) || !Enum.IsDefined(DirectionControl))
            throw new InvalidOperationException("串口校验位、停止位或双工配置无效");
        if (DuplexMode == SerialDuplexMode.FullDuplex && DirectionControl != SerialDirectionControl.Automatic)
            throw new InvalidOperationException("RTS 收发方向控制仅适用于半双工串口");
    }

    public TimeSpan GetTransmissionTime(int wireBytes) =>
        TimeSpan.FromSeconds(wireBytes * BitsPerCharacter / BaudRate);

    // 调用者在请求实际发送完成后开始应答计时，包含完整应答及 PC/设备处理裕量。
    public TimeSpan GetResponseTimeout(int responseRawLength) =>
        GetTransmissionTime(SerialTransportProtocol.GetMaxWireLength(responseRawLength)) +
        TimeSpan.FromMilliseconds(TimeoutMilliseconds +
            (DuplexMode == SerialDuplexMode.HalfDuplex ? TurnaroundDelayMilliseconds * 2 : 0));

    public TimeSpan GetIncompleteFrameTimeout(SerialProtocol protocol) =>
        GetTransmissionTime(SerialTransportProtocol.GetMaxWireLength(SerialTransportProtocol.GetMaxRawLength(protocol))) +
        TimeSpan.FromMilliseconds(Math.Max(1000, TimeoutMilliseconds * 2) + TurnaroundDelayMilliseconds * 2);
}
