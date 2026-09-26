using System.Globalization;
using System.Xml.Linq;

namespace EtherCAT.Esi;

/// <summary>
/// EtherCATInfo.xml（ESI，ETG.2000 从站描述文件）解析器
/// </summary>
public static class EsiParser
{
    /// <summary>
    /// 解析一个 ESI 文件，返回其中描述的全部设备（一个文件可含多个 Device）
    /// </summary>
    public static List<EsiDevice> Parse(string filePath)
    {
        var doc = XDocument.Load(filePath);
        return Parse(doc, filePath);
    }

    public static List<EsiDevice> Parse(XDocument doc, string sourceFile = "")
    {
        var result = new List<EsiDevice>();
        var root = doc.Root;
        if (root == null) return result;

        foreach (var info in root.Name.LocalName == "EtherCATInfo" ? new[] { root } : root.Descendants("EtherCATInfo"))
        {
            uint vendorId = ParseNumber(info.Element("Vendor")?.Element("Id")?.Value);
            string vendorName = info.Element("Vendor")?.Element("Name")?.Value?.Trim() ?? string.Empty;

            foreach (var device in info.Descendants("Device"))
            {
                var d = new EsiDevice
                {
                    SourceFile = sourceFile,
                    VendorId = vendorId,
                    VendorName = vendorName
                };

                var type = device.Element("Type");
                if (type != null)
                {
                    d.TypeText = type.Value.Trim();
                    d.ProductCode = ParseNumber(type.Attribute("ProductCode")?.Value);
                    d.RevisionNumber = ParseNumber(type.Attribute("RevisionNo")?.Value);
                }
                d.Name = device.Element("Name")?.Value?.Trim() ?? string.Empty;

                // 同步管理器
                int smIndex = 0;
                foreach (var sm in device.Elements("Sm"))
                {
                    var esm = new EsiSyncManager
                    {
                        Index = smIndex++,
                        StartAddress = (ushort)ParseNumber(sm.Attribute("StartAddress")?.Value),
                        ControlByte = (byte)ParseNumber(sm.Attribute("ControlByte")?.Value),
                        DefaultSize = (int)ParseNumber(sm.Attribute("DefaultSize")?.Value),
                        MinSize = (int)ParseNumber(sm.Attribute("MinSize")?.Value),
                        MaxSize = (int)ParseNumber(sm.Attribute("MaxSize")?.Value),
                        Enable = sm.Attribute("Enable")?.Value != "0"
                    };
                    d.SyncManagers.Add(esm);
                }

                // RxPdo / TxPdo
                foreach (var pdo in device.Elements("RxPdo"))
                    d.RxPdos.Add(ParsePdo(pdo));
                foreach (var pdo in device.Elements("TxPdo"))
                    d.TxPdos.Add(ParsePdo(pdo));

                // 部分厂商把 PDO 放在 <Profile><Dictionary> 之外，也可能出现在 <Modules> 中，这里一并收集
                foreach (var pdo in device.Descendants("RxPdo").Where(e => e.Parent?.Name.LocalName != "Device"))
                    d.RxPdos.Add(ParsePdo(pdo));
                foreach (var pdo in device.Descendants("TxPdo").Where(e => e.Parent?.Name.LocalName != "Device"))
                    d.TxPdos.Add(ParsePdo(pdo));

                // 分布式时钟
                var dc = device.Element("Dc");
                if (dc != null)
                {
                    d.SupportsDc = true;
                    var opMode = dc.Descendants("OpMode").FirstOrDefault();
                    if (opMode != null)
                    {
                        d.AssignActivate = (ushort)ParseNumber(opMode.Element("AssignActivate")?.Value);
                        d.CycleTimeSync0 = (int)ParseNumber(opMode.Element("CycleTimeSync0")?.Value);
                        d.ShiftTimeSync0 = (int)ParseNumber(opMode.Element("ShiftTimeSync0")?.Value);
                    }
                }

                result.Add(d);
            }
        }

        return result;
    }

    private static EsiPdo ParsePdo(XElement element)
    {
        var pdo = new EsiPdo
        {
            Index = (ushort)ParseNumber(element.Element("Index")?.Value),
            Name = element.Element("Name")?.Value?.Trim() ?? string.Empty,
            Fixed = element.Attribute("Fixed")?.Value == "1",
            Mandatory = element.Attribute("Mandatory")?.Value == "1"
        };

        int sm = (int)ParseNumber(element.Attribute("Sm")?.Value);
        pdo.SyncManager = element.Attribute("Sm") != null ? sm : -1;

        foreach (var entry in element.Elements("Entry"))
        {
            pdo.Entries.Add(new EsiPdoEntry
            {
                Index = (ushort)ParseNumber(entry.Element("Index")?.Value),
                SubIndex = (byte)ParseNumber(entry.Element("SubIndex")?.Value),
                BitLength = (int)ParseNumber(entry.Element("BitLen")?.Value),
                Name = entry.Element("Name")?.Value?.Trim() ?? string.Empty,
                DataType = entry.Element("DataType")?.Value?.Trim() ?? string.Empty
            });
        }

        return pdo;
    }

    /// <summary>
    /// ESI 中数值多为 "#x1A2B" 十六进制形式，也可能是十进制或 "#b1010"
    /// </summary>
    internal static uint ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        text = text.Trim();
        try
        {
            if (text.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
                return uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (text.StartsWith("#b", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt32(text[2..], 2);
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (text.StartsWith("#", StringComparison.Ordinal))
                return uint.Parse(text.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture);
            return uint.Parse(text, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }
}
