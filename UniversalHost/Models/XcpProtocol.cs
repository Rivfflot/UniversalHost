using System;
using System.Buffers.Binary;

namespace UniversalHost.Models;

/// <summary>
/// XCP协议
/// </summary>
public static class XcpProtocol
{
    /// <summary>
    /// XCP 主机下发 CMD 指令。XCP数据第一个字节PID = CMD。
    /// </summary>
    public enum Command : byte
    {
        // STD 标准指令
        Std_Connect = 0xFF,
        Std_Disconnect = 0xFE,
        Std_GetStatus = 0xFD,
        Std_Sync = 0xFC,
        Std_GetCommModeInfo = 0xFB,
        Std_GetId = 0xFA,
        Std_Request = 0xF9,
        Std_GetSeed = 0xF8,
        Std_Unlock = 0xF7,
        Std_SetMta = 0xF6,
        Std_Upload = 0xF5,
        Std_ShortUpload = 0xF4,
        Std_BuildChecksum = 0xF3,
        Std_TransportLayerCmd = 0xF2,
        Std_UserCmd = 0xF1,
        //CAL 标定指令
        Cal_Download = 0xF0,
        Cal_DownloadNext = 0xEF,
        Cal_DownloadMax = 0xEE,
        Cal_ShortDownload = 0xED,
        Cal_ModifyBits = 0xEC,
        //PAG 页面切换指令        
        Pag_SetCalPage = 0xEB,
        Pag_GetCalPage = 0xEA,
        Pag_GetPagProcessorInfo = 0xE9,
        Pag_GetSegmentInfo = 0xE8,
        Pag_GetPageInfo = 0xE7,
        Pag_SetSegmentMode = 0xE6,
        Pag_GetSegmentMode = 0xE5,
        Pag_CopyCalPage = 0xE4,
        //DAQ 数据采集指令-基本
        Daq_SetDaqPtr = 0xE2,
        Daq_WriteDaq = 0xE1,
        Daq_SetDaqListMode = 0xE0,
        Daq_GetDaqListMode = 0xDF,
        Daq_StartStopDaqList = 0xDE,
        Daq_StartStopSynch = 0xDD,
        Daq_GetDaqClock = 0xDC,
        Daq_ReadDaq = 0xDB,
        Daq_GetDaqProcessorInfo = 0xDA,
        Daq_GetDaqResolutionInfo = 0xD9,
        Daq_GetDaqEventInfo = 0xD7,
        Daq_WriteDaqMultiple = 0xC7,
        Daq_DtoCtrProperties = 0xC5,
        //DAQ 数据采集指令-静态
        Daq_ClearDaqList = 0xE3,
        Daq_GetDaqListInfo = 0xD8,
        //DAQ 数据采集指令-动态
        Daq_FreeDaq = 0xD6,
        Daq_AllocDaq = 0xD5,
        Daq_AllocOdt = 0xD4,
        Daq_AllocOdtEntry = 0xD3,
        //PGM 编程指令
        Pgm_ProgramStart = 0xD2,
        Pgm_ProgramClear = 0xD1,
        Pgm_Program = 0xD0,
        Pgm_ProgramReset = 0xCF,
        Pgm_GetPgmProcessorInfo = 0xCE,
        Pgm_GetSectorInfo = 0xCD,
        Pgm_ProgramPrepare = 0xCC,
        Pgm_ProgramFormat = 0xCB,
        Pgm_ProgramNext = 0xCA,
        Pgm_ProgramMax = 0xC9,
        Pgm_ProgramVerify = 0xC8,
        //TIM 同步指令
        Tim_TimeCorrectionProperties = 0xC6,
    }
    public enum Error : byte
    {
        ///<summary>从机同步响应</summary>
        CmdSynch = 0x00,
        ///<summary>指令未执行</summary>
        CmdBusy = 0x10,
        ///<summary>指令未执行，DAQ正在运行</summary>
        DaqActive = 0x11,
        ///<summary>指令未执行，PGM正在运行</summary>
        PgmActive = 0x12,
        ///<summary>未知/未实现指令</summary>
        CmdUnkown = 0x20,
        ///<summary>指令格式错误</summary>
        CmdSyntax = 0x21,
        ///<summary>格式正确，指令参数超出范围</summary>
        OutOfRange = 0x22,
        ///<summary>内存区域为写保护</summary>
        WriteProtected = 0x23,
        ///<summary>内存无法访问</summary>
        AccessDenied = 0x24,
        ///<summary>访问拒绝，需要解锁</summary>
        AccessLocked = 0x25,
        ///<summary>选中页无效</summary>
        PageNotValid = 0x26,
        ///<summary>选中页模式无效</summar>
        ModeNotValid = 0x27,
        ///<summary>选中区无效</summary>
        SegmentNotValid = 0x28,
        ///<summary>顺序错误</summary>
        Sequence = 0x29,
        ///<summary>DAQ 设置无效</summary>
        DaqConfig = 0x2A,
        ///<summary>内存溢出</summary>
        MemoryOverflow = 0x30,
        ///<summary>通用错误</summary>
        Generic = 0x31,
        ///<summary>验证错误</summary>
        Verify = 0x32,
    }
    public enum Event : byte
    {
        /// <summary>从机开始恢复</summary>
        ResumeMode = 0x00,
        /// <summary>从机存储在非易失存储器中的DAQ设置已清除</summary>
        ClearDaq = 0x01,
        /// <summary>从机已将DAQ设置保存在非易失存储器</summary>
        StoreDaq = 0x02,
        /// <summary>从机已将CAL标定数据存储于非易失存储器</summary>
        StoreCal = 0x03,
        /// <summary>从机请求重新开始超时</summary>
        CmdPending = 0x05,
        /// <summary>从机Daq处理器过载</summary>
        DaqOverload = 0x06,
        /// <summary>从机已关闭会话</summary>
        SessionTerminated = 0x07,
        /// <summary>传输附加时间戳</summary>
        TimeSync = 0x08,
        /// <summary>STIM超时</summary>
        StimTimeout = 0x09,
        /// <summary>从机进入睡眠模式</summary>
        Sleep = 0x0A,
        /// <summary>从机唤醒</summary>
        WakeUp = 0x0B,
        /// <summary>用户定义事件</summary>
        User = 0xFE,
        /// <summary>传输层特定事件</summary>
        Transport = 0xFF,
    }
    public enum CtoResponcePid : byte
    {
        Positive = 0xFF,
        Error = 0xFE,
        Event = 0xFD,
        Service = 0xFC
    }
    public static void AssertResponse(ReadOnlySpan<byte> response, Command cmd)
    {
        if (response.IsEmpty)
        {
            throw new Exception($"[XCP 协议错误] 命令 {cmd} 收到空的网络载荷。");
        }
        CtoResponcePid pid = (CtoResponcePid)response[0];
        // 正常的肯定响应
        if (pid == CtoResponcePid.Positive)
        {
            // 验证通过，不做任何动作
            return;
        }

        // 下位机返回错误响应 (ERR)
        if (pid == CtoResponcePid.Error)
        {
            // response[1] Error Code
            byte rawErrCode = response.Length > 1 ? response[1] : (byte)0x00;
            // 尝试转换为强类型枚举，如果转换成功就输出日志，否则输出十六进制
            if (Enum.IsDefined(typeof(Error), rawErrCode))
            {
                var err = (Error)rawErrCode;
                throw new Exception($"[下位机拒绝命令] 执行 {cmd} 失败！错误原因: {err} (0x{rawErrCode:X2})。");
            }
            else
            {
                throw new Exception($"[下位机拒绝命令] 执行 {cmd} 失败！收到未知错误码: 0x{rawErrCode:X2}。");
            }
        }

        // 如果 PID 是 0xFD，说明下位机在推送 DTO 的时，上位机发了命令，下位机回错了
        else
        {
            throw new Exception($"[XCP 串线错误] 正在等待命令 {cmd} 的响应，却意外收到了 DAQ 的 DTO 数据帧 (PID: 0x{pid:X2})。请检查接收线程的分流路由。");
        }
    }
    public interface IXcpCommand<TParams, TResponse>
    {
        static abstract int Encode(Span<byte> buffer, TParams args);
        static abstract TResponse Decode(ReadOnlySpan<byte> response, bool isLittleEndian);
    }
    public readonly struct EmptyParam;

    /// <summary>
    /// 计算数值数组的AG长度
    /// </summary>
    /// <param name="ag"></param>
    /// <param name="sizeOfSymbolInBytes"></param>
    /// <returns></returns>
    public static byte CalculateAgLenOfValue(Std.AddressGranularity ag, byte sizeOfSymbolInBytes)
    {
        byte bytesPerAg = ag switch
        {
            Std.AddressGranularity.Byte => 1,
            Std.AddressGranularity.Word => 2,
            _ => 4,
        };
        return (byte)((sizeOfSymbolInBytes + bytesPerAg - 1) / bytesPerAg);
    }


    /// <summary>
    /// 按照AG计算值的字节数组长度。例如，AG=Word，值为int8时返回2，其中高位应当补0.
    /// </summary>
    /// <param name="ag"></param>
    /// <param name="sizeOfSymbolInBytes">值的原始字节数组长度</param>
    /// <returns></returns>
    public static byte CalculateByteLenOfValue(Std.AddressGranularity ag, byte sizeOfSymbolInBytes)
    {
        return ag switch
        {
            Std.AddressGranularity.Byte => sizeOfSymbolInBytes,
            Std.AddressGranularity.Word => Math.Max((byte)(sizeOfSymbolInBytes), (byte)2),
            _ => Math.Max((byte)(sizeOfSymbolInBytes), (byte)4),
        };
    }
    /// <summary>
    /// 标定时，将值的数组按照AG和端序转换为待发送数据写入待发送数组。
    /// </summary>
    /// <param name="isLittleEndian"></param>
    /// <param name="ag"></param>
    /// <param name="src"></param>
    /// <param name="des"></param>
    /// <returns></returns>
    public static int CopyWithPedding(bool isLittleEndian, Std.AddressGranularity ag, ReadOnlySpan<byte> src, Span<byte> des)
    {
        byte lenInByte = CalculateByteLenOfValue(ag, (byte)src.Length);

        if (isLittleEndian)
        {
            src[..lenInByte].CopyTo(des);
        }
        else
        {
            Span<byte> a = stackalloc byte[8];
            src.CopyTo(a);
            if (src.Length > 1)
            {
                a[..lenInByte].Reverse();
            }
            a[..lenInByte].CopyTo(des);
        }
        return lenInByte;
    }

    public static class Std
    {
        /// <summary>
        /// 地址粒度 (AG - Address Granularity)
        /// </summary>
        public enum AddressGranularity
        {
            /// <summary>8-bit 字节寻址 (1 Byte)</summary>
            Byte = 1,
            /// <summary>16-bit 字寻址 (2 Bytes)</summary>
            Word = 2,
            /// <summary>32-bit 双字寻址 (4 Bytes)</summary>
            DWord = 4
        }
        public struct ConnectResponse
        {
            ///<summary>是否支持CAL PAG 标定 分页</summary>
            public bool CanCalPag;
            ///<summary>是否支持 DAQ 采集测量</summary>
            public bool CanDaq;
            ///<summary>是否支持刺激数据注入</summary>
            public bool CanStim;
            ///<summary>是否支持Flash刷写</summary>
            public bool CanPgm;
            ///<summary>true小端序，false大端序</summary>
            public bool IsLittleEndian;
            ///<summary>地址粒度,8/16/32位</summary>
            public AddressGranularity Granularity;
            ///<summary>是否支持块传输模式</summary>
            public bool BlockMode;
            ///<summary>是否存在可选的传输层特定参数</summary>
            public bool ExistOptionalParams;
            ///<summary>最大CTO(指令)帧长度</summary>
            public byte MaxCtoLen;
            ///<summary>最大DTO(数据)帧长度</summary>
            public ushort MaxDtoLen;
            ///<summary>XCP 协议版本</summary>
            public byte XcpProtocolVer;
            ///<summary>XCP 传输版本</summary>
            public byte XcpTransferVer;
        }
        public struct GetStatusResponse
        {
            ///<summary>从机保存标定数据请求执行中。从机正在将当前标定页数据写入非易失存储器</summary>
            public bool StoreCalReq;
            ///<summary>从机保存获取数据请求执行中。从机正在将当前获取数据列表写入非易失存储器</summary>
            public bool StoreDaqReq;
            ///<summary>从机清除获取数据请求执行中。从机正在清楚当前非易失存储器中的获取数据列表</summary>
            public bool ClearDaqReq;
            ///<summary>从机DAQ高频数据获取执行中。</summary>
            public bool DaqRunning;
            ///<summary>从机恢复模式激活。代表下位机支持断电记忆并在重新上电后自动恢复之前的 DAQ 配置。</summary>
            public bool Resume;
            ///<summary>从机CAL/PAG当前是否被保护。</summary>
            public bool CalPagProtected;
            ///<summary>从机DAQ当前是否被保护。</summary>
            public bool DaqProtected;
            ///<summary>从机STIM当前是否被保护。</summary>
            public bool StimProtected;
            ///<summary>从机PGM当前是否被保护。</summary>
            public bool PgmProtected;
            ///<summary>从机会话ID。</summary>
            public UInt16 SessionId;
            public UInt16 PreviousSessionId;
        }
        public enum ConnectMode : byte
        {
            Normal = 0x00,
            UserDefined = 0x01,
        }
        public readonly struct ConnectCommand : IXcpCommand<ConnectMode, ConnectResponse>
        {
            public static XcpProtocol.Command CommandCode => Command.Std_Connect;
            public static int Encode(Span<byte> buffer, ConnectMode p)
            {
                buffer[0] = (byte)Command.Std_Connect;
                buffer[1] = (byte)p;
                return 2;
            }
            public static ConnectResponse Decode(ReadOnlySpan<byte> response, bool _)
            {
                XcpProtocol.AssertResponse(response, CommandCode);
                ConnectResponse res = default;
                // --- Byte 1: Resource ---
                res.CanCalPag = (response[1] & 0b0000_0001) != 0; // Bit 0: CAL/PAG
                res.CanDaq = (response[1] & 0b0000_0100) != 0; // Bit 2: DAQ
                res.CanStim = (response[1] & 0b0000_1000) != 0; // Bit 3: STIM
                res.CanPgm = (response[1] & 0b0001_0000) != 0; // Bit 4: PGM

                // --- Byte 2: Comm Mode Basic ---
                // Bit 0: 0 = Little Endian, 1 = Big Endian
                res.IsLittleEndian = (response[2] & 0b0000_0001) == 0;

                // Bit 1-2: Address Granularity
                var granularityBit = (response[2] & 0b0000_0110) >> 1;
                res.Granularity = granularityBit switch
                {
                    0b00 => Std.AddressGranularity.Byte, // 0: 8-bit (绝大多数单片机)
                    0b01 => Std.AddressGranularity.Word, // 1: 16-bit (C2000 系列DSP)
                    0b10 => Std.AddressGranularity.DWord,  // 2: 32-bit
                    _ => throw new NotSupportedException($"XCP connect 寻址粒度值不支持: {granularityBit}")
                };

                res.BlockMode = (response[2] & 0b0100_0000) != 0; // Bit 6: SLAVE_BLOCK_MODE
                res.ExistOptionalParams = (response[2] & 0b1000_0000) != 0; // Bit 7: OPTIONAL_TL_PARAMS

                // --- Byte 3: MAX_CTO ---
                res.MaxCtoLen = response[3];

                // --- Byte 4-5: MAX_DTO ---
                if (res.IsLittleEndian)
                {
                    res.MaxDtoLen = BinaryPrimitives.ReadUInt16LittleEndian(response[4..6]);
                }
                else
                {
                    res.MaxDtoLen = BinaryPrimitives.ReadUInt16BigEndian(response[4..6]);
                }

                // --- Byte 6-7: Version ---
                res.XcpProtocolVer = response[6];
                res.XcpTransferVer = response[7];

                return res;
            }
        }
        public readonly struct DisconnectCommand : IXcpCommand<EmptyParam, bool>
        {
            public static int Encode(Span<byte> buffer, EmptyParam p)
            {
                buffer[0] = (byte)Command.Std_Disconnect;
                return 1;
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool _)
            {
                XcpProtocol.AssertResponse(response, Command.Std_Disconnect);
                return true;
            }
        }
        public readonly struct GetStatusCommand : IXcpCommand<EmptyParam, GetStatusResponse>
        {
            public static int Encode(Span<byte> buffer, EmptyParam p)
            {
                buffer[0] = (byte)Command.Std_GetStatus;
                return 1;
            }
            public static GetStatusResponse Decode(ReadOnlySpan<byte> response, bool isLittleEndian)
            {
                XcpProtocol.AssertResponse(response, Command.Std_GetStatus);
                GetStatusResponse res = default;
                //byte 1 当前会话状态
                res.StoreCalReq = (response[1] & 0b0000_0001) != 0; // Bit 0: CAL/PAG
                res.StoreDaqReq = (response[1] & 0b0000_0100) != 0;//bit 2 : DAQ
                res.ClearDaqReq = (response[1] & 0b0000_1000) != 0;//bit 3
                res.DaqRunning = (response[1] & 0b0100_0000) != 0;//6
                res.Resume = (response[1] & 0b1000_0000) != 0;//7

                //byte 2 当前资源保护状态
                res.CalPagProtected = (response[2] & 0b0000_0001) != 0;//0
                res.DaqProtected = (response[2] & 0b0000_0100) != 0;//2
                res.StimProtected = (response[2] & 0b0000_1000) != 0;//3
                res.PgmProtected = (response[2] & 0b0001_0000) != 0;//4

                //byte 3 保留
                //byte 4-5 会话ID
                if (isLittleEndian)
                {
                    res.SessionId = BinaryPrimitives.ReadUInt16LittleEndian(response[4..6]);
                }
                else
                {
                    res.SessionId = BinaryPrimitives.ReadUInt16BigEndian(response[4..6]);
                }
                return res;
            }
        }
        public readonly struct SyncCommand : IXcpCommand<EmptyParam, bool>
        {
            public static int Encode(Span<byte> buffer, EmptyParam p)
            {
                buffer[0] = (byte)Command.Std_Sync;
                return 1;
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool _)
            {
                if (response.Length >= 2 && response[0] == 0xFE && response[1] == 0x00)
                {
                    return true; // 完美的同步成功响应
                }
                throw new Exception($"[同步失败] 下位机对 SYNCH 无响应或返回了非预期的 PID: 0x{response[0]:X2}");
            }
        }
        public readonly struct SetMtaParams
        {
            public bool IsLittleEndian { get; init; }
            public byte Extension { get; init; }
            public uint Address { get; init; }
        }
        public readonly struct SetMtaCommand : IXcpCommand<SetMtaParams, bool>
        {
            public static int Encode(Span<byte> buffer, SetMtaParams p)
            {
                buffer[0] = (byte)Command.Std_SetMta;
                buffer[1] = 0;
                buffer[2] = 0;
                buffer[3] = p.Extension;
                if (p.IsLittleEndian)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4, 4), p.Address);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), p.Address);
                }
                return 8;
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool _)
            {
                XcpProtocol.AssertResponse(response, Command.Std_SetMta);
                return true;
            }
        }
        public readonly struct UploadCommand : IXcpCommand<byte, byte[]>
        {
            public static int Encode(Span<byte> buffer, byte p)
            {
                buffer[0] = (byte)Command.Std_Upload;
                buffer[1] = p;
                return 2;
            }
            public static byte[] Decode(ReadOnlySpan<byte> response, bool _)
            {
                XcpProtocol.AssertResponse(response, Command.Std_Upload);
                return response[1..].ToArray();
            }
        }
        public readonly struct ShortUploadParams
        {
            public bool IsLittleEndian { get; init; }
            public byte Number { get; init; }
            public byte Extension { get; init; }
            public uint Address { get; init; }
        }
        public readonly struct ShortUploadCommand : IXcpCommand<ShortUploadParams, byte[]>
        {
            public static int Encode(Span<byte> buffer, ShortUploadParams p)
            {
                buffer[0] = (byte)Command.Std_ShortUpload;
                buffer[1] = p.Number;
                buffer[2] = 0;
                buffer[3] = p.Extension;
                if (p.IsLittleEndian)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4, 4), p.Address);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), p.Address);
                }
                return 8;
            }
            public static byte[] Decode(ReadOnlySpan<byte> response, bool _)
            {
                XcpProtocol.AssertResponse(response, Command.Std_ShortUpload);
                return response[1..].ToArray();
            }
        }
        public readonly struct UserCmdParams
        {
            public byte SubCommand { get; init; }
            public byte[] Parameters { get; init; }
        }
        public readonly struct UserCmdCommand : IXcpCommand<UserCmdParams, bool>
        {
            public static int Encode(Span<byte> buffer, UserCmdParams p)
            {
                buffer[0] = (byte)Command.Std_UserCmd;
                buffer[1] = p.SubCommand;
                p.Parameters.CopyTo(buffer[2..]);
                return p.Parameters.Length + 2;
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool _)
            {
                XcpProtocol.AssertResponse(response, Command.Std_UserCmd);
                return true;
            }
        }
    }

    public static class Cal
    {
        public readonly struct DownloadParams
        {
            public bool IsLittleEndian { get; init; }
            /// <summary> Data number in AG </summary>
            public Std.AddressGranularity Ag { get; init; }
            public ReadOnlyMemory<byte> Data { get; init; }
        }
        public readonly struct DownloadCommand : IXcpCommand<DownloadParams, bool>
        {
            public static int Encode(Span<byte> buffer, DownloadParams p)
            {
                buffer[0] = (byte)Command.Cal_Download;
                buffer[1] = p.Ag switch
                {
                    Std.AddressGranularity.Byte => (byte)p.Data.Length,
                    Std.AddressGranularity.Word => Math.Max((byte)(p.Data.Length / 2), (byte)1),
                    _ => Math.Max((byte)(p.Data.Length / 4), (byte)1),
                };

                return 2 + CopyWithPedding(p.IsLittleEndian, p.Ag, p.Data.Span, buffer[2..]);
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool _)
            {
                XcpProtocol.AssertResponse(response, Command.Cal_Download);
                return true;
            }
        }
        public readonly struct ShortDownloadParams
        {
            public bool IsLittleEndian { get; init; }
            /// <summary> Data number in AG </summary>
            public Std.AddressGranularity Ag { get; init; }
            public byte Extension { get; init; }
            public uint Address { get; init; }
            public ReadOnlyMemory<byte> Data { get; init; }
        }
        public readonly struct ShortDownloadCommand : IXcpCommand<ShortDownloadParams, bool>
        {
            public static int Encode(Span<byte> buffer, ShortDownloadParams p)
            {
                buffer[0] = (byte)Command.Cal_Download;
                buffer[1] = p.Ag switch
                {
                    Std.AddressGranularity.Byte => (byte)p.Data.Length,
                    Std.AddressGranularity.Word => Math.Max((byte)(p.Data.Length / 2), (byte)1),
                    _ => Math.Max((byte)(p.Data.Length / 4), (byte)1),
                };
                buffer[2] = 0;
                buffer[3] = p.Extension;
                if (p.IsLittleEndian)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..8], p.Address);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(buffer[4..8], p.Address);
                }
                return 8 + CopyWithPedding(p.IsLittleEndian, p.Ag, p.Data.Span, buffer[8..]);
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool _)
            {
                XcpProtocol.AssertResponse(response, Command.Cal_Download);
                return true;
            }
        }
    }
    public static class Daq
    {
        #region static DAQ
        public readonly struct ClearDaqListParams
        {
            public bool IsLittleEndian { get; init; }
            public ushort DaqList { get; init; }
        }
        /// <summary>
        ///  清除指定的DAQ列表。传入要清除的列表号。如果指定列表不可用，返回ERR_OUT_OF_RANGE
        /// </summary>
        public readonly struct ClearDaqListCommand : IXcpCommand<ClearDaqListParams, bool>
        {
            public static int Encode(Span<byte> buffer, ClearDaqListParams p)
            {
                buffer[0] = (byte)Command.Daq_ClearDaqList;
                buffer[1] = 0;
                if (p.IsLittleEndian)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(2, 2), p.DaqList);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), p.DaqList);
                }
                return 4;
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool _)
            {
                XcpProtocol.AssertResponse(response, Command.Daq_ClearDaqList);
                return true;
            }
        }
        public readonly struct SetDaqPtrParams
        {
            public bool IsLittleEndian { get; init; }
            public ushort DaqListNumber { get; init; }
            /// <summary>该DAQ列表中ODT编号</summary>
            public byte OdtNumber { get; init; }
            /// <summary>该ODT的中的Entry编号 </summary>
            public byte OdtEntryNumber { get; init; }
        }
        public readonly struct SetDaqPtrCommand : IXcpCommand<SetDaqPtrParams, bool>
        {
            public static int Encode(Span<byte> buffer, SetDaqPtrParams p)
            {
                buffer[0] = (byte)Command.Daq_SetDaqPtr;
                buffer[1] = 0;
                if (p.IsLittleEndian)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(2, 2), p.DaqListNumber);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), p.DaqListNumber);
                }
                buffer[4] = p.OdtNumber;
                buffer[5] = p.OdtEntryNumber;
                return 6;
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool _)
            {
                XcpProtocol.AssertResponse(response, Command.Daq_SetDaqPtr);
                return true;
            }
        }
        public readonly struct WriteDaqParams
        {
            public bool IsLittleEndian { get; init; }
            /// <summary>允许传输表示的数据刺激元素位的状态</summary>
            public byte BitOffset { get; init; }
            /// <summary>DAQ大小(in AG)/// </summary>
            public byte SizeOfDaq { get; init; }
            public byte Extension { get; init; }
            public uint Address { get; init; }
        }
        public readonly struct WriteDaqCommand : IXcpCommand<WriteDaqParams, bool>
        {
            public static int Encode(Span<byte> buffer, WriteDaqParams p)
            {
                buffer[0] = (byte)Command.Daq_WriteDaq;
                buffer[1] = p.BitOffset;
                buffer[2] = p.SizeOfDaq;
                buffer[3] = p.Extension;
                if (p.IsLittleEndian)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4, 4), p.Address);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), p.Address);
                }
                return 8;
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool _)
            {
                XcpProtocol.AssertResponse(response, Command.Daq_WriteDaq);
                return true;
            }
        }
        public readonly struct SetDaqListModeParams
        {
            public bool IsLittleEndian { get; init; }
            /// <summary>false 数据采集，true 数据刺激 </summary>
            public bool Direction { get; init; }
            /// <summary>false 关闭时间戳，true 使能时间戳 </summary>
            public bool Timestamp { get; init; }
            /// <summary>false 发送DTO包含PID，true 发送DTO不含PID  </summary>
            public bool PidOff { get; init; }
            public ushort DaqList { get; init; }
            public ushort EventChannel { get; init; }
            /// <summary>传输速率，必须 >=1 </summary>
            public byte RatePrescaler { get; init; }
            /// <summary> 0xFF 优先级最高 </summary>
            public byte Priority { get; init; }
        }
        public readonly struct SetDaqListModeCommand : IXcpCommand<SetDaqListModeParams, bool>
        {
            public static int Encode(Span<byte> buffer, SetDaqListModeParams p)
            {
                buffer[0] = (byte)Command.Daq_SetDaqListMode;
                buffer[1] = (byte)((p.Direction ? (1 << 1) : 0) | (p.Timestamp ? (1 << 4) : 0) | (p.PidOff ? (1 << 5) : 0));
                if (p.IsLittleEndian)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(2, 2), p.DaqList);
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(4, 2), p.EventChannel);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), p.DaqList);
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(4, 2), p.EventChannel);
                }
                buffer[6] = p.RatePrescaler;
                buffer[7] = p.Priority;
                return 8;
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool _)
            {
                XcpProtocol.AssertResponse(response, Command.Daq_SetDaqListMode);
                return true;
            }
        }
        public readonly struct GetDaqListModeParams
        {
            public bool IsLittleEndian { get; init; }
            public ushort DaqList { get; init; }
        }
        public struct GetDaqListModeResponse
        {
            /// <summary>true 已选择DAQ list，false未选择 </summary>
            public bool Selected;
            /// <summary>false DAQ，true STIM </summary>
            public bool Direction;
            /// <summary>false 时间戳关闭，true打开</summary>
            public bool Timestamp;
            /// <summary>false DTO PID打开，true关闭 </summary>
            public bool PidOff;
            /// <summary>false DAQ未运行，true运行 </summary>
            public bool Running;
            /// <summary>true 此DAQ列表在RESUME中使用 </summary>
            public bool Resume;
            public ushort EventChannel;
            public byte Prescaler;
            public byte Priority;
        }
        public readonly struct GetDaqListModeCommand : IXcpCommand<GetDaqListModeParams, GetDaqListModeResponse>
        {
            public static int Encode(Span<byte> buffer, GetDaqListModeParams p)
            {
                buffer[0] = (byte)Command.Daq_GetDaqListMode;
                buffer[1] = 0;
                if (p.IsLittleEndian)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(2, 2), p.DaqList);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), p.DaqList);
                }
                return 4;
            }
            public static GetDaqListModeResponse Decode(ReadOnlySpan<byte> response, bool isLittleEndian)
            {
                XcpProtocol.AssertResponse(response, Command.Daq_GetDaqListMode);
                GetDaqListModeResponse res = new();

                // byte 1
                res.Selected = (response[1] & 0b0000_0001) != 0;
                res.Direction = (response[1] & 0b0000_0010) != 0;
                res.Timestamp = (response[1] & 0b0001_0000) != 0;
                res.PidOff = (response[1] & 0b0010_0000) != 0;
                res.Running = (response[1] & 0b0100_0000) != 0;
                res.Resume = (response[1] & 0b1000_0000) != 0;
                if (isLittleEndian)
                {
                    res.EventChannel = BinaryPrimitives.ReadUInt16LittleEndian(response[4..6]);
                }
                else
                {
                    res.EventChannel = BinaryPrimitives.ReadUInt16BigEndian(response[4..6]);
                }
                res.Prescaler = response[6];
                res.Priority = response[7];
                return res;
            }
        }
        public enum DaqListMode : byte
        {
            Stop = 0x00,
            Start = 0x01,
            Select = 0x02,
        }

        public readonly struct StartStopDaqListParams
        {
            public bool IsLittleEndian { get; init; }
            public DaqListMode Mode { get; init; }
            public ushort DaqList { get; init; }
        }
        /// <summary>
        /// 启动/停止选择的DAQ list。返回：第一PID号
        /// </summary>
        public readonly struct StartStopDaqListCommand : IXcpCommand<StartStopDaqListParams, byte>
        {
            public static int Encode(Span<byte> buffer, StartStopDaqListParams p)
            {
                buffer[0] = (byte)Command.Daq_StartStopDaqList;
                buffer[1] = (byte)p.Mode;
                if (p.IsLittleEndian)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(2, 2), p.DaqList);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), p.DaqList);
                }
                return 4;
            }
            public static byte Decode(ReadOnlySpan<byte> response, bool _)
            {
                XcpProtocol.AssertResponse(response, Command.Daq_StartStopDaqList);
                return response[1];
            }
        }
        public readonly struct StartStopSynchCommand : IXcpCommand<DaqListMode, bool>
        {
            public static int Encode(Span<byte> buffer, DaqListMode p)
            {
                buffer[0] = (byte)Command.Daq_StartStopSynch;
                buffer[1] = (byte)p;
                return 2;
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool _)
            {
                XcpProtocol.AssertResponse(response, Command.Daq_StartStopSynch);
                return true;
            }
        }
        public readonly struct GetDaqListInfoParams
        {
            public bool IsLittleEndian { get; init; }
            public ushort DaqList { get; init; }
        }
        public struct GetDaqListInfoResponse
        {
            /// <summary>false 可更改DAQ列表配置，true 不可更改</summary>
            public bool IsPredefined;
            /// <summary>false 事件通道可更改，true 不可更改</summary>
            public bool IsEventFixed;
            public bool CanDaq;
            public bool CanStim;
            public byte MaxOdt;
            public byte MaxOdtEntries;
            public ushort FixedEvent;
        }
        public readonly struct GetDaqListInfoCommand : IXcpCommand<GetDaqListInfoParams, GetDaqListInfoResponse>
        {
            public static int Encode(Span<byte> buffer, GetDaqListInfoParams p)
            {
                buffer[0] = (byte)Command.Daq_GetDaqListInfo;
                buffer[1] = 0;
                if (p.IsLittleEndian)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(2, 2), p.DaqList);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), p.DaqList);
                }
                return 2;
            }
            public static GetDaqListInfoResponse Decode(ReadOnlySpan<byte> response, bool isLittleEndian)
            {
                XcpProtocol.AssertResponse(response, Command.Daq_GetDaqListInfo);
                GetDaqListInfoResponse res;
                //1
                res.IsPredefined = (response[1] & 0b0000_0001) != 0;
                res.IsEventFixed = (response[1] & 0b0000_0010) != 0;
                res.CanDaq = (response[1] & 0b0000_0100) != 0;
                res.CanStim = (response[1] & 0b0000_1000) != 0;
                res.MaxOdt = response[2];
                res.MaxOdtEntries = response[3];
                if (isLittleEndian)
                {
                    res.FixedEvent = BinaryPrimitives.ReadUInt16LittleEndian(response[4..6]);
                }
                else
                {
                    res.FixedEvent = BinaryPrimitives.ReadUInt16BigEndian(response[4..6]);
                }
                return res;
            }
        }

        #endregion

        #region Dynamic DAQ
        public readonly struct FreeDaqCommand : IXcpCommand<EmptyParam, bool>
        {
            public static int Encode(Span<byte> buffer, EmptyParam p)
            {
                buffer[0] = (byte)Command.Daq_FreeDaq;
                return 1;
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool isLittleEndian)
            {
                XcpProtocol.AssertResponse(response, Command.Daq_FreeDaq);
                return true;
            }
        }
        public readonly struct AllocDaqParams
        {
            public bool IsLittleEndian { get; init; }
            /// <summary>分配的DAQ数量</summary>
            public ushort DaqCount { get; init; }
        }
        public readonly struct AllocDaqCommand : IXcpCommand<AllocDaqParams, bool>
        {
            public static int Encode(Span<byte> buffer, AllocDaqParams p)
            {
                buffer[0] = (byte)Command.Daq_AllocDaq;
                buffer[1] = 0;
                if (p.IsLittleEndian)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(2, 2), p.DaqCount);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), p.DaqCount);
                }
                return 4;
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool isLittleEndian)
            {
                XcpProtocol.AssertResponse(response, Command.Daq_AllocDaq);
                return true;
            }
        }
        public readonly struct AllocOdtParams
        {
            public bool IsLittleEndian { get; init; }
            /// <summary>DAQ列表号</summary>
            public ushort DaqList { get; init; }
            /// <summary>分配的ODT数量</summary>
            public byte OdtCount { get; init; }
        }
        public readonly struct AllocOdtCommand : IXcpCommand<AllocOdtParams, bool>
        {
            public static int Encode(Span<byte> buffer, AllocOdtParams p)
            {
                buffer[0] = (byte)Command.Daq_AllocOdt;
                buffer[1] = 0;
                if (p.IsLittleEndian)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(2, 2), p.DaqList);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), p.DaqList);
                }
                buffer[4] = p.OdtCount;
                return 5;
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool isLittleEndian)
            {
                XcpProtocol.AssertResponse(response, Command.Daq_AllocOdt);
                return true;
            }
        }
        public readonly struct AllocOdtEntryParams
        {
            public bool IsLittleEndian { get; init; }
            /// <summary>DAQ列表号</summary>
            public ushort DaqList { get; init; }
            /// <summary>ODT号</summary>
            public byte OdtNumber { get; init; }
            /// <summary>待分配的ODT entries 数量</summary>
            public byte OdtEntriesCount { get; init; }
        }
        public readonly struct AllocOdtEntryCommand : IXcpCommand<AllocOdtEntryParams, bool>
        {
            public static int Encode(Span<byte> buffer, AllocOdtEntryParams p)
            {
                buffer[0] = (byte)Command.Daq_AllocOdtEntry;
                buffer[1] = 0;
                if (p.IsLittleEndian)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(2, 2), p.DaqList);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), p.DaqList);
                }
                buffer[4] = p.OdtNumber;
                buffer[5] = p.OdtEntriesCount;
                return 6;
            }
            public static bool Decode(ReadOnlySpan<byte> response, bool isLittleEndian)
            {
                XcpProtocol.AssertResponse(response, Command.Daq_AllocOdtEntry);
                return true;
            }
        }
        #endregion
    }

    public static class Pgm
    {

    }

}

