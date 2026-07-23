using ReactiveUI;
using ReactiveUI.SourceGenerators;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UniversalHost.Models;

public sealed class RingBuffer<T>
{
    private readonly T[] _buffer;
    public T[] Buffer => _buffer;
    private int _writeIndex;

    public int Capacity => _buffer.Length;
    public int Count { get; private set; }

    public RingBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentException("capacity must > 0");

        _buffer = new T[capacity];
    }

    // =========================
    // Write (wrap index)
    // =========================
    public void Add(T item)
    {
        _buffer[_writeIndex] = item;

        _writeIndex++;
        if (_writeIndex == Capacity)
            _writeIndex = 0;

        if (Count < Capacity)
            Count++;
    }

    // =========================
    // newest-first indexing
    // 0 = newest
    // =========================
    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

            int newestIndex = _writeIndex == 0 ? Capacity - 1 : _writeIndex - 1;

            int physicalIndex = newestIndex - index;
            if (physicalIndex < 0)
                physicalIndex += Capacity;

            return _buffer[physicalIndex];
        }
    }

    // =========================
    // contiguous export (newest → oldest)
    // =========================
    public void CopyToSpan(Span<T> dst)
    {
        if (dst.Length < Count)
            throw new ArgumentException("destination too small");

        int newestIndex = _writeIndex == 0 ? Capacity - 1 : _writeIndex - 1;

        for (int i = 0; i < Count; i++)
        {
            int idx = newestIndex - i;
            if (idx < 0)
                idx += Capacity;

            dst[i] = _buffer[idx];
        }
    }

    // =========================
    // snapshot (for ScottPlot fallback)
    // =========================
    public T[] ToArray()
    {
        var arr = new T[Count];
        CopyToSpan(arr);
        return arr;
    }

    public int WriteIndex => _writeIndex;
}
public abstract partial class SymbolRuntime : ReactiveObject
{
    public UserSymbolInfo Symbol { get; init; }

    // UI 绑定的属性保留在基类
    [Reactive] protected string _valueString = "";
    // 提供给外部的非泛型只读属性
    public abstract byte ValueSizeInBytes { get; }
    //画曲线图用
    public abstract RingBuffer<double> PlotHistory { get; }
    protected SymbolRuntime(UserSymbolInfo symbol)
    {
        Symbol = symbol;
    }
    public abstract bool UpdateValueFromBytes(ReadOnlySpan<byte> data, bool isLittleEndian);
    public abstract byte[]? StringToValue();
    public abstract string ValueToString();
    public abstract string ValueToStringWithoutUpdate();
    public abstract string? GetValueHistoryIndexString(int index);
    public abstract void GetBitsValue(Span<byte> data);
    //TEST
    public abstract void AddRandomData();

    // ================= 工厂方法：动态创建强类型实例 =================
    public static SymbolRuntime CreateSymbolRuntime(UserSymbolInfo symbol, int maxSaveLen)
    {
        return symbol.DataType switch
        {
            SymbolDataType.Int8 => new SymbolRuntime<sbyte>(symbol, maxSaveLen),
            SymbolDataType.Uint8 => new SymbolRuntime<byte>(symbol, maxSaveLen),
            SymbolDataType.Int16 => new SymbolRuntime<short>(symbol, maxSaveLen),
            SymbolDataType.Uint16 => new SymbolRuntime<ushort>(symbol, maxSaveLen),
            SymbolDataType.Int32 => new SymbolRuntime<int>(symbol, maxSaveLen),
            SymbolDataType.Uint32 => new SymbolRuntime<uint>(symbol, maxSaveLen),
            SymbolDataType.Int64 => new SymbolRuntime<long>(symbol, maxSaveLen),
            SymbolDataType.Uint64 => new SymbolRuntime<ulong>(symbol, maxSaveLen),
            SymbolDataType.Float32 => new SymbolRuntime<float>(symbol, maxSaveLen),
            SymbolDataType.Float64 => new SymbolRuntime<double>(symbol, maxSaveLen),
            SymbolDataType.Boolean => new SymbolRuntime<bool>(symbol, maxSaveLen),
            _ => new SymbolRuntime<byte>(symbol, maxSaveLen),//Unknown
        };
    }
}

public partial class SymbolRuntime<T> : SymbolRuntime where T : struct
{
    public T Value { get; private set; }
    private readonly CircularBuffer.CircularBuffer<T> _valuesHistory;
    private readonly RingBuffer<double> _plotHistory;
    public override RingBuffer<double> PlotHistory => _plotHistory;
    public override byte ValueSizeInBytes => (byte)Unsafe.SizeOf<T>();

    public SymbolRuntime(UserSymbolInfo symbol, int maxSaveLen)
                                     : base(symbol)
    {
        _valuesHistory = new CircularBuffer.CircularBuffer<T>(maxSaveLen);
        _plotHistory = new RingBuffer<double>(maxSaveLen);
    }
    /// <summary>
    /// 获取指定位置的String。保存CSV使用。
    /// </summary>
    /// <param name="index">最旧值为0</param>
    /// <returns></returns>
    public override string? GetValueHistoryIndexString(int index)
    {
        if (index < _valuesHistory.Size)
        {
            return _valuesHistory[index].ToString();
        }
        else
        {
            return null;
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ConvertToDouble(T value)
    {
        return value switch
        {
            bool bl => bl ? 1.0 : 0.0,
            byte b => b,
            sbyte sb => sb,
            short s => s,
            ushort us => us,
            int i => i,
            uint ui => ui,
            float f => f,
            long lo => lo,
            ulong ulo => ulo,
            double d => d,
            _ => Convert.ToDouble(value)
        };
    }
    /// <summary>
    /// 支持端序转换的数据解析
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool UpdateValueFromBytes(ReadOnlySpan<byte> data, bool isLittleEndian)
    {
        // 大多数设备是小端序，或大小为1字节，不需要翻转端序，直接快速读取
        if (isLittleEndian || ValueSizeInBytes == 1)
        {
            Value = MemoryMarshal.Read<T>(data);
        }
        else
        {
            // 需要翻转端序时，调用泛型安全的翻转读取方法
            Value = ReadWithByteSwap(data);
        }
        _valuesHistory.PushBack(Value);

        _plotHistory.Add(ConvertToDouble(Value));
        return true;
    }
    /// <summary>
    /// 借助 BinaryPrimitives 实现无分配、无装箱的端序翻转读取
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T ReadWithByteSwap(ReadOnlySpan<byte> data)
    {
        // 使用 Unsafe.As 变换类型，配合 typeof(T) 分支。
        if (typeof(T) == typeof(short))
        {
            short val = BinaryPrimitives.ReverseEndianness(MemoryMarshal.Read<short>(data));
            return Unsafe.As<short, T>(ref val);
        }
        if (typeof(T) == typeof(ushort))
        {
            ushort val = BinaryPrimitives.ReverseEndianness(MemoryMarshal.Read<ushort>(data));
            return Unsafe.As<ushort, T>(ref val);
        }
        if (typeof(T) == typeof(int))
        {
            int val = BinaryPrimitives.ReverseEndianness(MemoryMarshal.Read<int>(data));
            return Unsafe.As<int, T>(ref val);
        }
        if (typeof(T) == typeof(uint))
        {
            uint val = BinaryPrimitives.ReverseEndianness(MemoryMarshal.Read<uint>(data));
            return Unsafe.As<uint, T>(ref val);
        }
        if (typeof(T) == typeof(long))
        {
            long val = BinaryPrimitives.ReverseEndianness(MemoryMarshal.Read<long>(data));
            return Unsafe.As<long, T>(ref val);
        }
        if (typeof(T) == typeof(ulong))
        {
            ulong val = BinaryPrimitives.ReverseEndianness(MemoryMarshal.Read<ulong>(data));
            return Unsafe.As<ulong, T>(ref val);
        }
        if (typeof(T) == typeof(float))
        {
            // 单精度浮点数：先当做 Int32 翻转，再强转回 float
            int intVal = BinaryPrimitives.ReverseEndianness(MemoryMarshal.Read<int>(data));
            float val = Unsafe.As<int, float>(ref intVal);
            return Unsafe.As<float, T>(ref val);
        }
        if (typeof(T) == typeof(double))
        {
            // 双精度浮点数：先当做 Int64 翻转，再强转回 double
            long longVal = BinaryPrimitives.ReverseEndianness(MemoryMarshal.Read<long>(data));
            double val = Unsafe.As<long, double>(ref longVal);
            return Unsafe.As<double, T>(ref val);
        }

        // 兜底降级方案（基本不会触发，因为外层工厂限制了基本类型）
        return MemoryMarshal.Read<T>(data);
    }

    /// <summary>
    /// 将写入的字符串转换为值和发送字节，同时更新ValueHistory。标定时使用。
    /// </summary>
    /// <returns>表示转换后值的字节数组</returns>
    public override byte[]? StringToValue()
    {
        if (string.IsNullOrEmpty(ValueString)) throw new ArgumentNullException(nameof(ValueString), "输入数值为空");

        double val = double.Parse(ValueString);
        if (Symbol.MaxValue.HasValue && val > Symbol.MaxValue)
        {
            throw new ArgumentOutOfRangeException($"变量 {Symbol.Name} 写入值 {ValueString} 大于上限 {Symbol.MaxValue}");
        }
        else if (Symbol.MinValue.HasValue && val < Symbol.MinValue)
        {
            throw new ArgumentOutOfRangeException($"变量 {Symbol.Name} 写入值 {ValueString} 小于上限 {Symbol.MaxValue}");
        }

        T parsedValue = (T)Convert.ChangeType(ValueString, typeof(T));
        Value = parsedValue;
        ValueToString();
        _valuesHistory.PushBack(Value);
        byte[] bytes = new byte[ValueSizeInBytes];

        MemoryMarshal.Write(bytes, in parsedValue);

        return bytes;
    }

    public override string ValueToString()
    {

        if (_valuesHistory.IsEmpty) return "";

        ValueString = Value.ToString() ?? "";

        return ValueString;
    }
    public override string ValueToStringWithoutUpdate()
    {
        if (_valuesHistory.IsEmpty) return "";

        return Value.ToString() ?? "";
    }
    /// <summary>
    /// 测试用，添加随机数据
    /// </summary>
    public override void AddRandomData()
    {
        double r = Random.Shared.NextDouble() - 0.5;

        if (Value is bool bl)
        {
            Value = (T)(object)(r > 0 ? bl : !bl);
        }
        else if (Value is byte ub)
        {
            Value = (T)(object)(r > 0 ? (byte)(ub + 1) : (byte)(ub - 1));
        }
        else if (Value is sbyte sb)
        {
            Value = (T)(object)(r > 0 ? (sbyte)(sb + 1) : (sbyte)(sb - 1));
        }
        else if (Value is short s)
        {
            Value = (T)(object)(r > 0 ? (short)(s + 1) : (short)(s - 1));
        }
        else if (Value is ushort us)
        {
            Value = (T)(object)(r > 0 ? (ushort)(us + 1) : (ushort)(us - 1));
        }
        else if (Value is int i)
        {
            Value = (T)(object)(r > 0 ? (int)(i + 1) : (int)(i - 1));
        }
        else if (Value is uint ui)
        {
            Value = (T)(object)(r > 0 ? (uint)(ui + 1) : (uint)(ui - 1));
        }
        else if (Value is float f)
        {
            Value = (T)(object)(f += (float)r);
        }
        else if (Value is long l)
        {
            Value = (T)(object)(r > 0 ? (long)(l + 1) : (long)(l - 1));
        }
        else if (Value is ulong ul)
        {
            Value = (T)(object)(r > 0 ? (ulong)(ul + 1) : (ulong)(ul - 1));
        }
        else if (Value is double d)
        {
            Value = (T)(object)(d += r);
        }
        _valuesHistory.PushBack(Value);
        _plotHistory.Add(ConvertToDouble(Value));
    }

    public override void GetBitsValue(Span<byte> data)
    {
        if (_valuesHistory.Size == 0) return;
        T tempValue = this.Value;
        ReadOnlySpan<byte> valueBytes = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref tempValue), ValueSizeInBytes);
        for (int i = 0; i < data.Length; i++)
        {
            // i >> 3 等价于 i / 8（定位到哪个字节）
            // i & 7  等价于 i % 8（定位到字节内的哪一位）
            data[i] = (byte)((valueBytes[i >> 3] >> (i & 7)) & 1);
        }
    }
}
