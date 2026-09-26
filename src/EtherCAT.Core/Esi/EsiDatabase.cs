namespace EtherCAT.Esi;

/// <summary>
/// ESI 文件数据库：按 VendorId / ProductCode / Revision 索引从站描述
/// </summary>
public sealed class EsiDatabase
{
    private readonly List<EsiDevice> _devices = new();

    public IReadOnlyList<EsiDevice> Devices => _devices;

    public int Count => _devices.Count;

    /// <summary>加载目录下的所有 ESI（*.xml）文件</summary>
    public int LoadDirectory(string directory, bool recursive = true, IEthercatLog? log = null)
    {
        if (!Directory.Exists(directory))
            return 0;

        int loaded = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.xml",
                     recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            loaded += LoadFile(file, log);
        }
        return loaded;
    }

    public int LoadFile(string filePath, IEthercatLog? log = null)
    {
        try
        {
            var devices = EsiParser.Parse(filePath);
            foreach (var d in devices)
                _devices.Add(d);
            if (devices.Count > 0)
                log?.Log($"已加载 ESI：{Path.GetFileName(filePath)}（{devices.Count} 个设备）");
            return devices.Count;
        }
        catch (Exception ex)
        {
            log?.Log($"ESI 解析失败 {Path.GetFileName(filePath)}：{ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 查找匹配的从站描述：优先精确匹配（Vendor+Product+Revision），再逐级放宽
    /// </summary>
    public EsiDevice? Find(uint vendorId, uint productCode, uint revision)
    {
        EsiDevice? byVendorProduct = null;
        EsiDevice? byProductOnly = null;

        foreach (var d in _devices)
        {
            if (d.VendorId == vendorId && d.ProductCode == productCode)
            {
                if (d.RevisionNumber == revision)
                    return d;
                byVendorProduct ??= d;
            }
            else if (d.ProductCode == productCode)
            {
                byProductOnly ??= d;
            }
        }

        return byVendorProduct ?? byProductOnly;
    }

    public void Clear() => _devices.Clear();
}
