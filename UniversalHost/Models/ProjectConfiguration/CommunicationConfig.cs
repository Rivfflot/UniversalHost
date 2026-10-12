using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using System.IO.Ports;
using System.IO;
using System;

namespace UniversalHost.Models;

public enum CommunicationMode
{
    UDP,
    Serial
}

public enum SerialDuplexMode
{
    FullDuplex,
    HalfDuplex
}

public enum SerialDirectionControl
{
    Automatic,
    RtsHighWhileSending,
    RtsLowWhileSending
}

public partial class UdpConfig : ReactiveObject
{
    [Reactive] private string _localAddress = "127.0.0.1";
    [Reactive] private string _remoteAddress = "127.0.0.1";
    [Reactive] private int _iapLocalPort = 50002;
    [Reactive] private int _iapRemotePort = 50001;
    [Reactive] private int _xcpLocalPort = 50010;
    [Reactive] private int _xcpRemotePort = 50011;
    [Reactive] private int _timeoutMilliseconds = 50;
    [Reactive] private UInt16 _retryTimes = 10;
}

public partial class SerialConfig : ReactiveObject
{
    [Reactive] private string _selectedSerialPort = "";
    [Reactive] private int _baudRate = 115200;
    [Reactive] private Parity _paritySetting = System.IO.Ports.Parity.None;
    [Reactive] private StopBits _stopBitsSetting = StopBits.One;
    [Reactive] private SerialDuplexMode _duplexMode = SerialDuplexMode.FullDuplex;
    [Reactive] private SerialDirectionControl _directionControl = SerialDirectionControl.Automatic;
    [Reactive] private UInt16 _turnaroundDelayMilliseconds = 2;
    [Reactive] private bool _hasLocalEcho = false;
    [Reactive] private UInt16 _timeoutMilliseconds = 50;
    [Reactive] private UInt16 _retryTimes = 10;
}

public partial class IapConfig : ReactiveObject
{
    [Reactive] private string _iapFilePath = Path.Combine(AppContext.BaseDirectory);
    [Reactive] private UInt16 _waitForHandShakeTimeoutSeconds = 5;

    [Reactive] private UInt16 _waitForEraseTimeoutSeconds = 10;
    [Reactive] private UInt16 _waitForWriteTimeoutSeconds = 10;
    [Reactive] private UInt16 _waitForCheckTimeoutSeconds = 10;
    [Reactive] private UInt16 _waitForRebootTimeoutSeconds = 30;
}
