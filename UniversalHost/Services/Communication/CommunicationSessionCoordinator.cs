using System;
using System.Threading;
using UniversalHost.Models;
using UniversalHost.Services.Communication.Serial;

namespace UniversalHost.Services.Communication;

/// <summary>业务层原子占用会话，包含准备、连接、取消和资源清理阶段。</summary>
internal static class CommunicationSessionCoordinator
{
    private static readonly object Gate = new();
    private static SessionLease? _xcp;
    private static SessionLease? _iap;

    public static IDisposable AcquireXcp(CommunicationMode mode, SerialConnectionOptions? serial)
    {
        lock (Gate)
        {
            if (_iap?.Mode == CommunicationMode.Serial)
                throw new InvalidOperationException("串口 IAP 正在准备、执行或清理，请等待升级会话结束后连接设备");
            if (_xcp != null)
                throw new InvalidOperationException("XCP 正在连接、已连接或正在断开，请勿重复连接");
            if (mode == CommunicationMode.Serial && serial?.DuplexMode != SerialDuplexMode.FullDuplex)
                throw new InvalidOperationException("半双工串口仅支持 IAP，禁止连接 XCP；请按实际物理链路设置双工模式");
            var lease = new SessionLease(mode, isXcp: true);
            _xcp = lease;
            PublishStatus();
            return lease;
        }
    }

    public static IDisposable AcquireIap(CommunicationMode mode)
    {
        lock (Gate)
        {
            if (_iap != null)
                throw new InvalidOperationException("IAP 会话尚未结束，请勿重复开始升级");
            if (mode == CommunicationMode.Serial && _xcp != null)
                throw new InvalidOperationException("请先断开设备 XCP 连接，再开始串口 IAP 升级");
            var lease = new SessionLease(mode, isXcp: false);
            _iap = lease;
            PublishStatus();
            return lease;
        }
    }

    private static void PublishStatus()
    {
        GlobalStatus.Instance.IsXcpSessionActive = _xcp != null;
        GlobalStatus.Instance.IsIapRunning = _iap != null;
        GlobalStatus.Instance.IsSerialIapActive = _iap?.Mode == CommunicationMode.Serial;
    }

    private sealed class SessionLease(CommunicationMode mode, bool isXcp) : IDisposable
    {
        public CommunicationMode Mode { get; } = mode;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            lock (Gate)
            {
                if (isXcp && ReferenceEquals(_xcp, this))
                    _xcp = null;
                if (!isXcp && ReferenceEquals(_iap, this))
                    _iap = null;
                PublishStatus();
            }
        }
    }
}
