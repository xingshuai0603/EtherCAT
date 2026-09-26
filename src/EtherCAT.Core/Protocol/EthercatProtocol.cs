namespace EtherCAT.Protocol;

/// <summary>
/// EtherCAT 数据报命令（ETG.1000.4 / SOEM ec_cmdtype）
/// </summary>
public enum EthercatCommand : byte
{
    Nop = 0x00,
    Aprd = 0x01,   // Auto Increment Read
    Apwr = 0x02,   // Auto Increment Write
    Aprw = 0x03,   // Auto Increment Read Write
    Fprd = 0x04,   // Configured Address Read
    Fpwr = 0x05,   // Configured Address Write
    Fprw = 0x06,   // Configured Address Read Write
    Brd = 0x07,    // Broadcast Read
    Bwr = 0x08,    // Broadcast Write
    Brw = 0x09,    // Broadcast Read Write
    Lrd = 0x0A,    // Logical Read
    Lwr = 0x0B,    // Logical Write
    Lrw = 0x0C,    // Logical Read Write
    Armw = 0x0D,   // Auto Increment Read Multiple Write
    Frmw = 0x0E    // Configured Read Multiple Write
}

/// <summary>
/// ESC（EtherCAT Slave Controller）寄存器地址，与 SOEM ec_type.h 保持一致
/// </summary>
public static class Esc
{
    public const ushort Type = 0x0000;
    public const ushort PortDes = 0x0007;
    public const ushort EscSup = 0x0008;
    public const ushort StationAddress = 0x0010;
    public const ushort Alias = 0x0012;
    public const ushort DlControl = 0x0100;
    public const ushort DlPort = 0x0101;
    public const ushort DlAlias = 0x0103;
    public const ushort DlStatus = 0x0110;
    public const ushort AlControl = 0x0120;
    public const ushort AlStatus = 0x0130;
    public const ushort AlStatusCode = 0x0134;
    public const ushort PdiControl = 0x0140;
    public const ushort IrqMask = 0x0200;
    public const ushort RxErrorCounter = 0x0300;
    public const ushort WatchdogCounter = 0x0442;
    public const ushort EepConfig = 0x0500;
    public const ushort EepControl = 0x0502;
    public const ushort EepStatus = 0x0502;
    public const ushort EepAddress = 0x0504;
    public const ushort EepData = 0x0508;

    public const ushort Fmmu0 = 0x0600;
    public const ushort Sm0 = 0x0800;
    public const ushort Sm1 = 0x0808;
    public const ushort Sm2 = 0x0810;
    public const ushort Sm3 = 0x0818;

    public static ushort Fmmu(int n) => (ushort)(Fmmu0 + n * 0x10);
    public static ushort Sm(int n) => (ushort)(Sm0 + n * 0x08);
    public static ushort SmStatus(int n) => (ushort)(Sm(n) + 0x05);
    public static ushort SmActivate(int n) => (ushort)(Sm(n) + 0x06);
    public static ushort SmControl(int n) => (ushort)(Sm(n) + 0x07);

    // Distributed Clocks
    public const ushort DcTime0 = 0x0900;
    public const ushort DcSystemTime = 0x0910;
    public const ushort DcSystemTimeOffset = 0x0920;
    public const ushort DcSystemTimeDelay = 0x0928;
    public const ushort DcControlUnit = 0x0980;
    public const ushort DcSyncActivation = 0x0981;
    public const ushort DcStart0 = 0x0990;
    public const ushort DcCycle0 = 0x09A0;
    public const ushort DcCycle1 = 0x09A4;

    public const ushort EtherType = 0x88A4;
    public const int MaxFrameSize = 1518;
    public const int EthernetHeaderSize = 14;
    public const int EthercatHeaderSize = 2;
    public const int DatagramHeaderSize = 10;
    public const int WorkCounterSize = 2;
    public const int DatagramFollows = 0x8000;

    /// <summary>配置站地址起始（SOEM EC_NODEOFFSET）</summary>
    public const ushort NodeOffset = 0x1000;
    /// <summary>过程数据物理内存起始</summary>
    public const ushort DpramBase = 0x1000;

    /// <summary>自动增量寻址：第 n 个从站（1-based）的 ADP</summary>
    public static ushort AutoIncrementAddress(int slavePosition) => (ushort)(0x10000 - slavePosition);
}

/// <summary>
/// AL（应用层）状态
/// </summary>
public enum AlState : byte
{
    None = 0x00,
    Init = 0x01,
    PreOperational = 0x02,
    Boot = 0x03,
    SafeOperational = 0x04,
    Operational = 0x08,
    ErrorAck = 0x10
}

/// <summary>
/// SII（Slave Information Interface）EEPROM 类别
/// </summary>
public static class Sii
{
    /// <summary>EEPROM 中类别区起始字地址</summary>
    public const int CategoryStartWord = 0x0040;
    public const int CategoryStartByte = CategoryStartWord * 2;

    public const int CatStrings = 10;
    public const int CatGeneral = 30;
    public const int CatFmmu = 40;
    public const int CatSyncManager = 41;
    public const int CatTxPdo = 50;
    public const int CatRxPdo = 51;
    public const int CatDistributedClock = 60;

    // 固定区（字地址）
    public const int VendorIdWord = 0x0008;
    public const int ProductCodeWord = 0x000A;
    public const int RevisionWord = 0x000C;
    public const int SerialWord = 0x000E;
    public const int BootRxMailboxWord = 0x0014;
    public const int BootTxMailboxWord = 0x0016;
    public const int StdRxMailboxWord = 0x0018;
    public const int StdTxMailboxWord = 0x001A;
    public const int MailboxProtocolWord = 0x001C;

    // EEPROM 状态机
    public const ushort CommandNop = 0x0000;
    public const ushort CommandRead = 0x0100;
    public const ushort CommandWrite = 0x0201;
    public const ushort CommandReload = 0x0300;
    public const ushort StatusBusy = 0x8000;
    public const ushort StatusErrorMask = 0x7800;
    public const ushort StatusRead64 = 0x0040;
    public const ushort StatusNack = 0x2000;
}

/// <summary>
/// 邮箱协议类型
/// </summary>
public static class Mailbox
{
    public const int Error = 0x00;
    public const int Aoe = 0x01;
    public const int Eoe = 0x02;
    public const int Coe = 0x03;
    public const int Foe = 0x04;
    public const int Soe = 0x05;
    public const int Voe = 0x0F;

    /// <summary>邮箱头长度：Length(2) + Address(2) + Priority(1) + Type/Counter(1)</summary>
    public const int HeaderSize = 6;

    public static byte BuildTypeField(int type, int counter) => (byte)(type | ((counter & 0x0F) << 4));
    public static int GetType(byte field) => field & 0x0F;
    public static int GetCounter(byte field) => (field >> 4) & 0x0F;
}

/// <summary>
/// CoE（CANopen over EtherCAT）服务与命令
/// </summary>
public static class Coe
{
    public const int HeaderSize = 2;

    // 服务（CoE 头高 4 位）
    public const int Emergency = 0x01;
    public const int SdoRequest = 0x02;
    public const int SdoResponse = 0x03;
    public const int TxPdo = 0x04;
    public const int RxPdo = 0x05;
    public const int TxPdoRemoteRequest = 0x06;
    public const int RxPdoRemoteRequest = 0x07;
    public const int SdoInformation = 0x08;

    // SDO 命令
    public const byte DownloadInitiate = 0x21;
    public const byte DownloadExpedited = 0x23;
    public const byte DownloadInitiateCa = 0x31;
    public const byte UploadInitiate = 0x40;
    public const byte UploadInitiateCa = 0x50;
    public const byte UploadSegment = 0x60;
    public const byte Abort = 0x80;

    // 标准对象字典索引
    public const ushort SmCommType = 0x1C00;
    public const ushort PdoAssign = 0x1C10;
    public const ushort RxPdoAssign = 0x1C12;
    public const ushort TxPdoAssign = 0x1C13;
    public const ushort RxPdoMappingBase = 0x1600;
    public const ushort TxPdoMappingBase = 0x1A00;

    /// <summary>组合 CoE 头：低 9 位 number，高 4 位 service</summary>
    public static ushort BuildHeader(int number, int service) => (ushort)((number & 0x1FF) | ((service & 0x0F) << 12));
    public static int GetService(ushort header) => (header >> 12) & 0x0F;
    public static int GetNumber(ushort header) => header & 0x1FF;

    /// <summary>SDO 中止码</summary>
    public static string DescribeAbort(uint code) => code switch
    {
        0x05030000 => "Toggle bit not changed",
        0x05040000 => "SDO protocol timeout",
        0x05040001 => "Client/server command specifier not valid or unknown",
        0x05040005 => "Out of memory",
        0x06010000 => "Unsupported access to an object",
        0x06010001 => "Attempt to read a write only object",
        0x06010002 => "Attempt to write a read only object",
        0x06020000 => "Object does not exist in the object dictionary",
        0x06040041 => "Object cannot be mapped into the PDO",
        0x06040042 => "Number and length of objects to be mapped exceeds PDO length",
        0x06040043 => "General parameter incompatibility",
        0x06040047 => "General internal incompatibility in the device",
        0x06060000 => "Access failed due to a hardware error",
        0x06070010 => "Data type does not match, length of service parameter does not match",
        0x06070012 => "Data type does not match, length of service parameter too high",
        0x06070013 => "Data type does not match, length of service parameter too low",
        0x06090011 => "Sub-index does not exist",
        0x06090030 => "Value range of parameter exceeded",
        0x06090031 => "Value of parameter written too high",
        0x06090032 => "Value of parameter written too low",
        0x08000000 => "General error",
        0x08000020 => "Data cannot be transferred or stored to the application",
        0x08000021 => "Data cannot be transferred or stored - local control",
        0x08000022 => "Data cannot be transferred or stored - device state",
        _ => $"Unknown abort code 0x{code:X8}"
    };
}

/// <summary>
/// AL 状态码说明（常见部分）
/// </summary>
public static class AlStatusCode
{
    public static string Describe(ushort code) => code switch
    {
        0x0000 => "No error",
        0x0011 => "Invalid requested state change",
        0x0012 => "Unknown requested state",
        0x0013 => "Bootstrap not supported",
        0x0014 => "No valid firmware",
        0x0015 => "Invalid mailbox configuration",
        0x0016 => "Invalid mailbox configuration (PREOP->SAFEOP)",
        0x0017 => "Invalid sync manager configuration",
        0x0018 => "No valid inputs available",
        0x0019 => "No valid outputs",
        0x001A => "Synchronization error",
        0x001B => "Sync manager watchdog",
        0x001C => "Invalid sync manager types",
        0x001D => "Invalid output configuration",
        0x001E => "Invalid input configuration",
        0x001F => "Invalid watchdog configuration",
        0x0020 => "Slave needs cold start",
        0x0021 => "Slave needs INIT",
        0x0022 => "Slave needs PREOP",
        0x0023 => "Slave needs SAFEOP",
        0x0024 => "Invalid input mapping",
        0x0025 => "Invalid output mapping",
        0x0026 => "Inconsistent settings",
        0x0027 => "Freerun not supported",
        0x0028 => "Synchronization not supported",
        0x0029 => "Sync mode not supported",
        0x002A => "Invalid request of sync manager",
        0x002B => "Free run needs 3 buffer mode",
        0x002C => "No PLL range",
        0x002D => "Invalid DC SYNC configuration",
        0x002E => "Invalid DC latch configuration",
        0x002F => "PLL error",
        0x0030 => "DC sync IO error",
        0x0031 => "DC sync timeout error",
        0x0032 => "DC invalid sync cycle time",
        0x0035 => "DC invalid sync0 cycle time",
        0x0036 => "DC invalid sync1 cycle time",
        _ => $"Unknown AL status code 0x{code:X4}"
    };
}
