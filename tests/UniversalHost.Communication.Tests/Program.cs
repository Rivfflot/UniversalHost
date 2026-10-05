using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Ports;
using ReactiveUI.Reactive.Builder;
using UniversalHost.Models;
using UniversalHost.Services;
using UniversalHost.Services.Communication;
using UniversalHost.Services.Communication.Serial;

namespace UniversalHost.Communication.Tests;

internal static class Program
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static async Task<int> Main()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        (string Name, Func<Task> Run)[] tests =
        [
            ("COBS documented vectors and 254-byte ending block", () => Run(CobsVectors)),
            ("CRC and serial CONNECT documented vector", () => Run(ConnectVector)),
            ("Maximum XCP/IAP frames and trailing zero", () => Run(MaximumFrames)),
            ("Malformed COBS, length, CRC and merged XCP rejection", () => Run(InvalidFrames)),
            ("Continuous frames split at every byte boundary", () => Run(StreamBoundaries)),
            ("Bad delimiter recovery, overflow and incomplete frame expiry", () => Run(StreamRecovery)),
            ("Serial wire-time and parity/stop-bit budgets", () => Run(TimeBudgets)),
            ("Receive cancellation preserves an incomplete frame", ReceiveCancellation),
            ("Whole-frame writes serialize and synchronize once", SerializedWrites),
            ("Local echo cannot replace an identical data ACK", EchoHandling),
            ("Receive overflow discards until the next delimiter", ReceiveOverflow),
            ("Serial sessions exclude each other; UDP can coexist", () => Run(SessionRules)),
            ("Concurrent XCP/IAP starts have one winner", SessionRace),
            ("IAP per-frame flashing over an echoing half-duplex link", () => IapSequence(true)),
            ("IAP whole-image flashing over an echoing half-duplex link", () => IapSequence(false)),
            ("IAP wire sending does not consume the response timeout", IapSlowSending),
            ("IAP receive retries are bounded and repeat identical requests", IapRetries),
            ("IAP cancellation keeps exclusion through cleanup", IapCancellation),
            ("Empty BIN never opens transport and releases exclusion", IapEmptyFile),
            ("XCP CTO/DAQ, counters, corruption, sending and disconnect", XcpSequence),
            ("XCP receive failure stops tasks and releases the session", XcpReceiveFailure),
            ("XCP connect timeout releases transport before returning", XcpConnectTimeout),
            ("Failed serial opening releases XCP exclusion", () => Run(XcpOpenFailure))
        ];
        int failed = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run().WaitAsync(TimeSpan.FromSeconds(15));
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {test.Name}\n{ex}");
            }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
        return failed == 0 ? 0 : 1;
    }

    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Equal(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        Check(expected.SequenceEqual(actual), $"Expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}");
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Check(watch.Elapsed < TestTimeout, "Condition did not become true");
            await Task.Delay(5);
        }
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", ""));
    private static SerialConnectionOptions Options(bool halfDuplex = false, bool echo = false, int baud = 115200, ushort timeout = 50) =>
        new(new SerialConfig
        {
            SelectedSerialPort = "COM_TEST",
            BaudRate = baud,
            StopBitsSetting = StopBits.One,
            DuplexMode = halfDuplex ? SerialDuplexMode.HalfDuplex : SerialDuplexMode.FullDuplex,
            HasLocalEcho = echo,
            TimeoutMilliseconds = timeout
        });
    private static ProjectSettings Settings(string? file = null, bool halfDuplex = false, bool echo = false, ushort timeout = 50)
    {
        var settings = new ProjectSettings();
        settings.DeviceConfig.Mode = CommunicationMode.Serial;
        settings.DeviceConfig.DeviceID = 0x12;
        settings.SerialConfig.SelectedSerialPort = "COM_TEST";
        settings.SerialConfig.DuplexMode = halfDuplex ? SerialDuplexMode.HalfDuplex : SerialDuplexMode.FullDuplex;
        settings.SerialConfig.HasLocalEcho = echo;
        settings.SerialConfig.TimeoutMilliseconds = timeout;
        settings.SerialConfig.RetryTimes = 1;
        if (file != null) settings.IapConfig.IapFilePath = file;
        return settings;
    }
    private static byte[] XcpPacket(byte[] payload, ushort counter = 1)
    {
        byte[] frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), counter);
        payload.CopyTo(frame, 4);
        return frame;
    }
    private static byte[] IapPacket(byte stage, byte[] payload, byte device = 0x12)
    {
        byte[] frame = new byte[payload.Length + 7];
        frame[0] = 1;
        frame[1] = stage;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)payload.Length);
        frame[4] = device;
        payload.CopyTo(frame, 5);
        WriteCrc(frame);
        return frame;
    }
    private static void WriteCrc(byte[] frame) => BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(frame.Length - 2),
        Crc.Crc16Modbus.Calculate(frame.AsSpan(0, frame.Length - 2)));
    private static byte[] Wire(SerialProtocol protocol, byte[] packet)
    {
        byte[] raw = new byte[SerialTransportProtocol.GetMaxRawLength(protocol)];
        byte[] wire = new byte[SerialTransportProtocol.GetMaxWireLength(raw.Length)];
        int length = SerialTransportProtocol.Encode(protocol, packet, raw, wire);
        return wire[..length];
    }
    private static byte[] RawWire(byte[] raw)
    {
        byte[] wire = new byte[Cobs.GetMaxEncodedLength(raw.Length) + 1];
        int length = Cobs.Encode(raw, wire);
        return wire[..(length + 1)];
    }
    private static byte[] ReadWire(SerialProtocol protocol, byte[] bytes)
    {
        var decoder = new SerialFrameDecoder(protocol, TestTimeout);
        var packets = Feed(decoder, bytes);
        Check(packets.Count == 1, "A write must contain exactly one independent frame");
        return packets[0];
    }
    private static List<byte[]> Feed(SerialFrameDecoder decoder, byte[] data, long? timestamp = null)
    {
        var packets = new List<byte[]>();
        long now = timestamp ?? Stopwatch.GetTimestamp();
        foreach (byte value in data)
            if (decoder.TryReadByte(value, now, out var packet)) packets.Add(packet.ToArray());
        return packets;
    }
    private static byte[] Concat(params byte[][] chunks) => chunks.SelectMany(x => x).ToArray();

    private static void CobsVectors()
    {
        (byte[] Raw, byte[] Encoded)[] examples =
        [
            (Hex("00"), Hex("01 01")),
            (Hex("11 22"), Hex("03 11 22")),
            (Hex("11 00 22"), Hex("02 11 02 22")),
            ([], Hex("01")),
            (Hex("11 00 00"), Hex("02 11 01 01")),
            (Enumerable.Repeat((byte)0x11, 254).ToArray(),
                Concat([0xFF], Enumerable.Repeat((byte)0x11, 254).ToArray(), [1]))
        ];
        foreach (var example in examples)
        {
            byte[] output = new byte[Cobs.GetMaxEncodedLength(example.Raw.Length)];
            int length = Cobs.Encode(example.Raw, output);
            Equal(example.Encoded, output.AsSpan(0, length));
            byte[] decoded = new byte[example.Raw.Length];
            Check(Cobs.TryDecode(example.Encoded, decoded, out int count), "COBS vector did not decode");
            Equal(example.Raw, decoded.AsSpan(0, count));
        }
        byte[] omittedEnd = Concat([0xFF], Enumerable.Repeat((byte)0x11, 254).ToArray());
        Check(Cobs.TryDecode(omittedEnd, new byte[254], out int omittedLength) && omittedLength == 254,
            "Equivalent COBS without the empty ending block was rejected");
        var random = new Random(1701);
        foreach (int size in new[] { 1, 253, 254, 255, 508, 509, 1383, 65542 })
        {
            byte[] raw = new byte[size];
            random.NextBytes(raw);
            raw[^1] = 0;
            byte[] encoded = new byte[Cobs.GetMaxEncodedLength(size)];
            int length = Cobs.Encode(raw, encoded);
            Check(!encoded.AsSpan(0, length).Contains((byte)0), "COBS contained a delimiter");
            byte[] decoded = new byte[size];
            Check(Cobs.TryDecode(encoded.AsSpan(0, length), decoded, out int count) && count == size, "COBS boundary roundtrip failed");
            Equal(raw, decoded);
        }
    }

    private static void ConnectVector()
    {
        Check(Crc.Crc16Modbus.Calculate("123456789"u8) == 0x4B37, "CRC check vector failed");
        Check(Crc.Crc16Modbus.Calculate(Hex("00 02 00 01 00 FF 00")) == 0xEE5A, "CONNECT CRC differs from the contract");
        byte[] packet = Hex("02 00 01 00 FF 00");
        byte[] expected = Hex("01 02 02 02 01 02 FF 03 EE 5A 00");
        Equal(expected, Wire(SerialProtocol.Xcp, packet));
        Equal(packet, ReadWire(SerialProtocol.Xcp, expected));
    }

    private static void MaximumFrames()
    {
        byte[] xcp = XcpPacket(Enumerable.Repeat((byte)0x11, ushort.MaxValue).ToArray(), ushort.MaxValue);
        Equal(xcp, ReadWire(SerialProtocol.Xcp, Wire(SerialProtocol.Xcp, xcp)));
        byte[] iap = IapPacket(2, new byte[IapProtocol.MaxBytesPerFrame + 4]);
        Check(iap.Length == 1383 && SerialTransportProtocol.GetMaxWireLength(iap.Length) == 1390,
            "IAP cache budget differs from the contract");
        Equal(iap, ReadWire(SerialProtocol.Iap, Wire(SerialProtocol.Iap, iap)));
    }

    private static void InvalidFrames()
    {
        foreach (byte[] invalid in new[] { Array.Empty<byte>(), Hex("00"), Hex("03 11"), Hex("02 00"), Hex("FF 11") })
            Check(!Cobs.TryDecode(invalid, new byte[100], out _), "Malformed COBS was accepted");
        Check(!Cobs.TryDecode(Hex("03 11 22"), new byte[1], out _), "COBS output overflow was accepted");
        byte[] good = Hex("00 02 00 01 00 FF 00 EE 5A");
        foreach (int offset in new[] { 0, 1, 2, 3, 5, 7, 8 })
        {
            byte[] bad = good.ToArray();
            bad[offset] ^= 0x20;
            Check(Feed(new SerialFrameDecoder(SerialProtocol.Xcp, TestTimeout), RawWire(bad)).Count == 0,
                "A corrupted XCP frame was accepted");
        }
        byte[] wrongLength = good.ToArray();
        wrongLength[1] = 1;
        WriteCrc(wrongLength);
        Check(Feed(new SerialFrameDecoder(SerialProtocol.Xcp, TestTimeout), RawWire(wrongLength)).Count == 0,
            "CRC-valid XCP length mismatch was accepted");
        byte[] packet = XcpPacket([0xFF, 0]);
        Throws<ArgumentException>(() => Wire(SerialProtocol.Xcp, Concat(packet, packet)));
        byte[] merged = Concat([0], packet, packet, [0, 0]);
        WriteCrc(merged);
        Check(Feed(new SerialFrameDecoder(SerialProtocol.Xcp, TestTimeout), RawWire(merged)).Count == 0,
            "Multiple XCP packets in one raw frame were accepted");
        byte[] iap = IapPacket(2, [0, 0, 0, 0, 0]);
        iap[3]++;
        WriteCrc(iap);
        Check(Feed(new SerialFrameDecoder(SerialProtocol.Iap, TestTimeout), RawWire(iap)).Count == 0,
            "CRC-valid IAP length mismatch was accepted");
        Check(Feed(new SerialFrameDecoder(SerialProtocol.Xcp, TestTimeout), Wire(SerialProtocol.Iap, IapPacket(4, [0, 0xFF]))).Count == 0,
            "An IAP frame reached XCP");
    }

    private static void StreamBoundaries()
    {
        byte[] packet = Hex("02 00 01 00 FF 00");
        byte[] wire = Wire(SerialProtocol.Xcp, packet);
        byte[] stream = Concat([0, 0], wire, wire, wire);
        for (int split = 0; split <= stream.Length; split++)
        {
            var decoder = new SerialFrameDecoder(SerialProtocol.Xcp, TestTimeout);
            var frames = Feed(decoder, stream[..split]);
            frames.AddRange(Feed(decoder, stream[split..]));
            Check(frames.Count == 3, $"Split {split} lost a frame");
            foreach (byte[] frame in frames) Equal(packet, frame);
        }
        var partial = new SerialFrameDecoder(SerialProtocol.Xcp, TestTimeout);
        long now = Stopwatch.GetTimestamp();
        Check(Feed(partial, wire[..^1], now).Count == 0, "A read boundary ended a frame");
        partial.ExpireCandidate(now + Stopwatch.Frequency / 2);
        Check(Feed(partial, [0], now + Stopwatch.Frequency / 2).Count == 1, "Ordinary idle dropped a legal partial frame");
    }

    private static void StreamRecovery()
    {
        byte[] good = Wire(SerialProtocol.Iap, IapPacket(4, [0, 0xFF]));
        var bad = new SerialFrameDecoder(SerialProtocol.Iap, TestTimeout);
        Check(Feed(bad, Concat([0xFF, 1, 0], good)).Count == 1 && bad.RejectedFrames == 1,
            "Bad delimited COBS swallowed the next valid frame");
        Check(Feed(new SerialFrameDecoder(SerialProtocol.Iap, TestTimeout), Concat(good[..^1], good, good)).Count == 1,
            "Missing delimiter failed to resynchronize");
        var oversized = new SerialFrameDecoder(SerialProtocol.Iap, TestTimeout);
        byte[] junk = Enumerable.Repeat((byte)1, Cobs.GetMaxEncodedLength(1383) + 1).ToArray();
        Check(Feed(oversized, Concat(junk, good, good)).Count == 1, "Oversized candidate accepted its tail as a frame");
        var expired = new SerialFrameDecoder(SerialProtocol.Iap, TimeSpan.FromSeconds(1));
        long start = Stopwatch.GetTimestamp();
        Feed(expired, good[..3], start);
        expired.ExpireCandidate(start + 2 * Stopwatch.Frequency);
        Check(Feed(expired, Concat(good, good), start + 2 * Stopwatch.Frequency).Count == 1,
            "Expired candidate tail was accepted before a synchronization delimiter");
    }

    private static void TimeBudgets()
    {
        var options = Options();
        Check(options.BitsPerCharacter == 10, "8N1 character size is wrong");
        Check(Math.Abs(options.GetTransmissionTime(1390).TotalMilliseconds - 120.6597) < 0.01,
            "Maximum IAP request wire time is wrong");
        Check(options.GetResponseTimeout(12) > TimeSpan.FromMilliseconds(50), "ACK wire time was omitted");
        Check(options.GetIncompleteFrameTimeout(SerialProtocol.Iap) > TimeSpan.FromSeconds(1), "Residual timeout is a UART idle threshold");
        var config = new SerialConfig { SelectedSerialPort = "COM_TEST", ParitySetting = Parity.Odd, StopBitsSetting = StopBits.Two };
        Check(new SerialConnectionOptions(config).BitsPerCharacter == 12, "Parity/stop bits were omitted");
        config.StopBitsSetting = StopBits.None;
        Throws<InvalidOperationException>(() => new SerialConnectionOptions(config));
    }

    private static async Task ReceiveCancellation()
    {
        var port = new FakeCommService();
        await using var transport = new SerialTransportService(port, Options(), SerialProtocol.Xcp);
        byte[] packet = XcpPacket([0xFF, 0]);
        byte[] wire = Wire(SerialProtocol.Xcp, packet);
        port.Enqueue(wire[..4]);
        byte[] buffer = new byte[100];
        using var shortWait = new CancellationTokenSource(30);
        await ThrowsAsync<OperationCanceledException>(() => transport.ReceiveAsync(buffer, shortWait.Token));
        port.Enqueue(wire[4..]);
        using var wait = new CancellationTokenSource(TestTimeout);
        int count = await transport.ReceiveAsync(buffer, wait.Token);
        Equal(packet, buffer.AsSpan(0, count));
    }

    private static async Task SerializedWrites()
    {
        var port = new FakeCommService { SendDelay = TimeSpan.FromMilliseconds(30) };
        await using var transport = new SerialTransportService(port, Options(), SerialProtocol.Xcp);
        byte[] first = XcpPacket([0xFF, 0]);
        byte[] second = XcpPacket([0xFD], 2);
        await Task.WhenAll(transport.SendAsync(first), transport.SendAsync(second));
        Check(!port.WritesOverlapped && port.Writes.Count == 2, "Serial writes interleaved");
        Equal(Concat([0], Wire(SerialProtocol.Xcp, first)), port.Writes[0]);
        Equal(Wire(SerialProtocol.Xcp, second), port.Writes[1]);
    }

    private static async Task EchoHandling()
    {
        var port = new FakeCommService();
        await using var transport = new SerialTransportService(port, Options(true, true), SerialProtocol.Iap);
        byte[] identicalRequestAndAck = IapPacket(2, [0, 0, 0, 0, 0]);
        await transport.SendAsync(identicalRequestAndAck);
        port.Enqueue(port.Writes[0]);
        byte[] buffer = new byte[100];
        using var noAck = new CancellationTokenSource(30);
        await ThrowsAsync<OperationCanceledException>(() => transport.ReceiveAsync(buffer, noAck.Token));
        port.Enqueue(Wire(SerialProtocol.Iap, identicalRequestAndAck));
        using var ack = new CancellationTokenSource(TestTimeout);
        int count = await transport.ReceiveAsync(buffer, ack.Token);
        Equal(identicalRequestAndAck, buffer.AsSpan(0, count));
        // 两次重试的回显一并迟到时，每份回显都必须消费，仍需额外的设备 ACK。
        await transport.SendAsync(identicalRequestAndAck);
        await transport.SendAsync(identicalRequestAndAck);
        port.Enqueue(Concat(port.Writes[1], port.Writes[2]));
        using var onlyEchoes = new CancellationTokenSource(30);
        await ThrowsAsync<OperationCanceledException>(() => transport.ReceiveAsync(buffer, onlyEchoes.Token));
        port.Enqueue(Wire(SerialProtocol.Iap, identicalRequestAndAck));
        count = await transport.ReceiveAsync(buffer, ack.Token);
        Equal(identicalRequestAndAck, buffer.AsSpan(0, count));
    }

    private static async Task ReceiveOverflow()
    {
        var port = new FakeCommService();
        await using var transport = new SerialTransportService(port, Options(), SerialProtocol.Iap);
        byte[] frame = Wire(SerialProtocol.Iap, IapPacket(4, [0, 0xFF]));
        port.Enqueue(frame[..3]);
        port.FailRead(new SerialReceiveException("simulated overrun"));
        port.Enqueue(Concat(frame, frame));
        using var wait = new CancellationTokenSource(TestTimeout);
        int count = await transport.ReceiveAsync(new byte[100], wait.Token);
        Check(count == 9, "Overrun did not recover at the next complete frame");
    }

    private static void SessionRules()
    {
        Throws<InvalidOperationException>(() => CommunicationSessionCoordinator.AcquireXcp(CommunicationMode.Serial, Options(true)));
        using (var xcp = CommunicationSessionCoordinator.AcquireXcp(CommunicationMode.Serial, Options()))
        {
            Throws<InvalidOperationException>(() => CommunicationSessionCoordinator.AcquireIap(CommunicationMode.Serial));
            Throws<InvalidOperationException>(() => CommunicationSessionCoordinator.AcquireXcp(CommunicationMode.Serial, Options()));
        }
        using (var iap = CommunicationSessionCoordinator.AcquireIap(CommunicationMode.Serial))
        {
            Check(GlobalStatus.Instance.IsSerialIapActive, "IAP exclusion was not published");
            Throws<InvalidOperationException>(() => CommunicationSessionCoordinator.AcquireXcp(CommunicationMode.Serial, Options()));
            Throws<InvalidOperationException>(() => CommunicationSessionCoordinator.AcquireXcp(CommunicationMode.UDP, null));
            Throws<InvalidOperationException>(() => CommunicationSessionCoordinator.AcquireIap(CommunicationMode.Serial));
        }
        using var udpXcp = CommunicationSessionCoordinator.AcquireXcp(CommunicationMode.UDP, null);
        using var udpIap = CommunicationSessionCoordinator.AcquireIap(CommunicationMode.UDP);
    }

    private static async Task SessionRace()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using var barrier = new Barrier(2);
            async Task<IDisposable?> Start(bool xcp) => await Task.Run(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    return xcp ? CommunicationSessionCoordinator.AcquireXcp(CommunicationMode.Serial, Options())
                        : CommunicationSessionCoordinator.AcquireIap(CommunicationMode.Serial);
                }
                catch (InvalidOperationException) { return null; }
            });
            var leases = await Task.WhenAll(Start(true), Start(false));
            try { Check(leases.Count(x => x != null) == 1, "Concurrent serial sessions both acquired ownership"); }
            finally { foreach (var lease in leases) lease?.Dispose(); }
        }
    }

    private static IapService IapPeer(ProjectSettings settings, FakeCommService port, bool perFrame,
        List<byte>? stages = null, bool acknowledge = true)
    {
        port.OnSend = (bytes, ct) =>
        {
            byte[] request = ReadWire(SerialProtocol.Iap, bytes);
            byte stage = request[1];
            stages?.Add(stage);
            if (settings.SerialConfig.HasLocalEcho) port.Enqueue(bytes.ToArray());
            if (!acknowledge) return Task.CompletedTask;
            byte[] payload = stage switch
            {
                0 => [0, perFrame ? (byte)1 : (byte)0, 0x05, 0x5C],
                1 => [0],
                2 => Concat(request[5..9], [0]),
                _ => [0, 0xFF]
            };
            byte[] ack = IapPacket(stage, payload, settings.DeviceConfig.DeviceID);
            byte[] corrupt = ack.ToArray();
            corrupt[^1] ^= 0x10;
            // 同一个读取块里包含错误 CRC、其他从站/阶段，以及有效 ACK。
            port.Enqueue(Concat(RawWire(corrupt), Wire(SerialProtocol.Iap, IapPacket(0x06, [0, 0xFF], 0x34)),
                Wire(SerialProtocol.Iap, IapPacket(0xFE, [0, 0xFF], settings.DeviceConfig.DeviceID)), Wire(SerialProtocol.Iap, ack)));
            return Task.CompletedTask;
        };
        return new IapService(new ImmediateProgress<double>(), new ImmediateProgress<string>(), settings,
            () => new SerialTransportService(port, new SerialConnectionOptions(settings.SerialConfig), SerialProtocol.Iap));
    }

    private static async Task IapSequence(bool perFrame)
    {
        string file = Path.GetTempFileName();
        try
        {
            byte[] bin = Enumerable.Range(0, 3000).Select(x => (byte)x).ToArray();
            await File.WriteAllBytesAsync(file, bin);
            var settings = Settings(file, halfDuplex: true, echo: true);
            var port = new FakeCommService();
            var stages = new List<byte>();
            await IapPeer(settings, port, perFrame, stages).RunIapSequenceAsync(CancellationToken.None);
            byte[] expected = perFrame ? [0, 1, 4, 2, 2, 2, 3, 6, 7] : [0, 1, 2, 2, 2, 3, 4, 5, 6, 7];
            Equal(expected, stages.ToArray());
            var data = port.Writes.Select(x => ReadWire(SerialProtocol.Iap, x)).Where(x => x[1] == 2).ToArray();
            Check(data.Length == 3 && data[2].Length == 267, "Last IAP data frame was padded or lost");
            Equal(bin, Concat(data.Select(x => x[9..^2]).ToArray()));
            Check(port.IsDisposed && !GlobalStatus.Instance.IsSerialIapActive, "IAP resources/exclusion were retained");
        }
        finally { File.Delete(file); }
    }

    private static async Task IapSlowSending()
    {
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, new byte[1]);
            var port = new FakeCommService { SendDelay = TimeSpan.FromMilliseconds(80) };
            var settings = Settings(file, timeout: 10);
            await IapPeer(settings, port, false).RunIapSequenceAsync(CancellationToken.None);
            Check(port.Writes.Count == 8, "Serial sending consumed the ACK budget or triggered retries");
        }
        finally { File.Delete(file); }
    }

    private static async Task IapRetries()
    {
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, new byte[1]);
            var settings = Settings(file, timeout: 15);
            var port = new FakeCommService();
            var service = IapPeer(settings, port, false);
            var send = port.OnSend!;
            port.OnSend = (bytes, ct) => ReadWire(SerialProtocol.Iap, bytes)[1] == 1 ? Task.CompletedTask : send(bytes, ct);
            await ThrowsAsync<TimeoutException>(() => service.RunIapSequenceAsync(CancellationToken.None));
            Check(port.Writes.Count == 3, "IAP exceeded configured retry count");
            Equal(port.Writes[1], port.Writes[2]);
            Check(port.IsDisposed && !GlobalStatus.Instance.IsSerialIapActive, "Failed IAP retained exclusion");
        }
        finally { File.Delete(file); }
    }

    private static async Task IapCancellation()
    {
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, new byte[1]);
            var settings = Settings(file, halfDuplex: true);
            var port = new FakeCommService { FinishDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var cancel = new CancellationTokenSource();
            Task run = IapPeer(settings, port, false, acknowledge: false).RunIapSequenceAsync(cancel.Token);
            await Until(() => port.Writes.Count > 0);
            cancel.Cancel();
            await port.DisposalStarted.Task.WaitAsync(TestTimeout);
            Check(GlobalStatus.Instance.IsSerialIapActive, "Cancellation released exclusion before disposal");
            Throws<InvalidOperationException>(() => CommunicationSessionCoordinator.AcquireXcp(CommunicationMode.Serial, Options()));
            port.FinishDisposal.TrySetResult();
            await ThrowsAsync<OperationCanceledException>(() => run);
            Check(port.IsDisposed && !GlobalStatus.Instance.IsSerialIapActive, "Cancelled IAP did not release exclusion");
        }
        finally { File.Delete(file); }
    }

    private static async Task IapEmptyFile()
    {
        string file = Path.GetTempFileName();
        try
        {
            var settings = Settings(file);
            bool opened = false;
            var service = new IapService(new ImmediateProgress<double>(), new ImmediateProgress<string>(), settings,
                () => { opened = true; return new FakeCommService(); });
            await ThrowsAsync<InvalidDataException>(() => service.RunIapSequenceAsync(CancellationToken.None));
            Check(!opened && !GlobalStatus.Instance.IsSerialIapActive, "Empty BIN opened the serial link or retained exclusion");
        }
        finally { File.Delete(file); }
    }

    private static XcpClient XcpPeer(ProjectSettings settings, FakeCommService port, List<byte>? commands = null, bool daq = false)
    {
        ushort counter = ushort.MaxValue - 1;
        port.OnSend = (bytes, ct) =>
        {
            byte[] request = ReadWire(SerialProtocol.Xcp, bytes);
            byte command = request[4];
            commands?.Add(command);
            byte[] payload = command switch
            {
                0xFF => [0xFF, 0x05, 0, 0xFF, 0xFF, 0xFF, 1, 1],
                0xFD => [0xFF, 0, 0, 0, 0, 0],
                0xDE => [0xFF, 0x10],
                _ => [0xFF]
            };
            byte[] reply = Wire(SerialProtocol.Xcp, XcpPacket(payload, counter++));
            if (command == 0xF1)
            {
                byte[] asyncEvent = Wire(SerialProtocol.Xcp, XcpPacket([0xFD, 5], counter++));
                port.Enqueue(asyncEvent);
            }
            port.Enqueue(reply);
            if (daq && command == 0xDD && request[5] == 1)
            {
                byte[] sample = XcpPacket([0x10, 0, 0, 0, 0, 0, 0, 0, 4, 3, 2, 1], counter++);
                byte[] corrupted = Wire(SerialProtocol.Xcp, sample);
                corrupted[2] ^= 0x01;
                port.Enqueue(Concat(corrupted, Wire(SerialProtocol.Xcp, sample)));
            }
            return Task.CompletedTask;
        };
        return new XcpClient(settings, () => new SerialTransportService(port, new SerialConnectionOptions(settings.SerialConfig), SerialProtocol.Xcp));
    }

    private static async Task XcpSequence()
    {
        var port = new FakeCommService { SendDelay = TimeSpan.FromMilliseconds(80) };
        var settings = Settings(timeout: 10);
        var commands = new List<byte>();
        await using var client = XcpPeer(settings, port, commands, daq: true);
        await client.ConnectAsync();
        Check(GlobalStatus.Instance.IsConnected, "Serial XCP failed to connect");
        await Task.WhenAll(client.ExecuteUserCmd(1), client.ExecuteUserCmd(2));
        Check(commands.Count(x => x == 0xF1) == 2, "CTO silently retransmitted a command");
        port.SendDelay = TimeSpan.Zero;
        var symbol = new SymbolRuntime<uint>(new UserSymbolInfo { Name = "sample", Address = 0x1234, DataType = SymbolDataType.Uint32 }, 10);
        await client.StartDaq([symbol]);
        await Until(() => symbol.Value == 0x01020304);
        Check(client.ReceiveStatistics.Snapshot().DaqDecoded == 1, "Bad DAQ frame was accepted or valid DAQ was lost");
        await client.DisconnectAsync();
        Check(port.IsDisposed && !GlobalStatus.Instance.IsXcpSessionActive, "Disconnect retained serial ownership");
        int disconnect = commands.LastIndexOf(0xFE);
        Check(disconnect > 0 && commands[disconnect - 1] == 0xDE, "DAQ was not stopped before DISCONNECT");
        Check(!port.WritesOverlapped, "XCP writes overlapped");
    }

    private static async Task XcpReceiveFailure()
    {
        var port = new FakeCommService();
        await using var client = XcpPeer(Settings(), port);
        await client.ConnectAsync();
        port.FailRead(new IOException("simulated serial unplug"));
        await Until(() => port.IsDisposed && !GlobalStatus.Instance.IsXcpSessionActive);
        Check(!GlobalStatus.Instance.IsConnected, "Failed serial receive kept the device connected");
    }

    private static async Task XcpConnectTimeout()
    {
        var port = new FakeCommService();
        await using var client = new XcpClient(Settings(timeout: 10),
            () => new SerialTransportService(port, Options(timeout: 10), SerialProtocol.Xcp));
        await ThrowsAsync<TimeoutException>(() => client.ConnectAsync());
        Check(port.IsDisposed && !GlobalStatus.Instance.IsXcpSessionActive,
            "Failed CONNECT returned before closing its serial session");
    }

    private static void XcpOpenFailure()
    {
        Throws<IOException>(() => new XcpClient(Settings(), () => throw new IOException("simulated open failure")));
        Check(!GlobalStatus.Instance.IsXcpSessionActive, "Failed serial open retained the session");
    }

    private sealed class ImmediateProgress<T> : IProgress<T>
    {
        public void Report(T value) { }
    }
}
