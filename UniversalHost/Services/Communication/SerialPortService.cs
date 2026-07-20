using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace UniversalHost.Services.Communication;

public class SerialPortService : ICommService
{
    private readonly SerialPort _serialPort;

    public SerialPortService(string selectedPort, StopBits stopBits, int baudRate)
    {
        _serialPort = new SerialPort()
        {
            StopBits = stopBits,
            DataBits = 8,
            BaudRate = baudRate,
            PortName = selectedPort,
            ReadTimeout = 1
        };
        _serialPort.Open();
    }

    public void Send(ReadOnlySpan<byte> data)
    {
        _serialPort.BaseStream.Write(data);
    }
    public async Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await _serialPort.BaseStream.WriteAsync(data, ct);
        return data.Length;
    }
    public byte[]? Receive()
    {
        int bytesToRead = _serialPort.BytesToRead;
        if (bytesToRead == 0) return null;

        byte[] buffer = new byte[bytesToRead];
        int readBytes = _serialPort.Read(buffer, 0, bytesToRead);

        if (readBytes == 0) return null;
        if (readBytes < bytesToRead) Array.Resize(ref buffer, readBytes);

        return buffer;
    }
    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        // 串口的 BaseStream.ReadAsync 在没有数据时会保持挂起，直到有数据到来或被取消
        return await _serialPort.BaseStream.ReadAsync(buffer, ct);
    }

    public async ValueTask DisposeAsync()
    {
        // 先清空缓冲区，防止 Close 时发生死锁
        _serialPort.DiscardInBuffer();
        _serialPort.DiscardOutBuffer();
        _serialPort.Close();
        _serialPort.Dispose();

        await Task.CompletedTask;
    }

}

