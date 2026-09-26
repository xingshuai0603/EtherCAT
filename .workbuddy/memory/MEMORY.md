# 项目长期记忆：D:\Library\EtherCAT（.NET EtherCAT 主站）

## 工程约定

- 目标框架统一 net8.0；解决方案文件是 `EtherCAT.slnx`（.NET 10 SDK 生成，不是 .sln）。
- 分层：`EtherCAT.Core`（协议/链路/主站/ESI/设备层）→ `EtherCAT.App`(Avalonia) / `EtherCAT.Demo`(控制台)。
- 链路抽象 `IEthercatLink` 是唯一切换点：真实硬件用 `NpcapLink`（P/Invoke wpcap，需装 Npcap + 管理员权限），
  无硬件用 `SimulatedLink`（含仿真从站：ESC 寄存器/SII EEPROM/CoE 对象字典/伺服与 IO 行为模型）。
- ESI 文件放 `esi/`；主站按 VendorId+ProductCode+RevisionNo 匹配，匹配不到时依次退化到 CoE 动态映射、SII。
- 未引入第三方原生库，协议实现参考 SOEM（GPLv3/商业双授权）与 ETG 规范。

## 虚拟设备（仿真功能模块，无需硬件）

- 行为模型抽象 `ISlaveBehavior`：`SimulatedSlave` 在 PDO 写入后/周期模拟时调用 `OnAfterOutputWrite`/`Simulate(dt)`；
  具体型号行为由 `VirtualDriveBehavior`（CiA402 位置/速度/转矩积分、软限位、回零开关、故障注入）
  与 `VirtualIoBehavior`（数字量回环、模拟量正弦波生成、AO 斜率限制）实现。
- 描述生成：`VirtualDeviceFactory` 程序化生成 ESI（单轴伺服 VSD-1A、双轴 VSD-2A、64 点数字 IO VIO-6464、
  混合 IO VIO-MIX 32DI/32DO+8AI/4AO），可 `ExportEsiFiles` 导出成标准 EtherCATInfo.xml；
  主站仍走"读 ESI→匹配→配 PDO"正常流程。`SimulatedLink.CreateFromEsi(devices, repeat, driveOptions, ioOptions)` 自动挂行为模型。
- 演示：`dotnet run --project src/EtherCAT.Demo -- --virtual`；界面"仿真拓扑"下拉选"大型虚拟网络"即组同一条拓扑。
- 多轴驱动：第 2 轴对象索引 = 基准 + 0x800（AxisIndexStep）；`CiA402Drive` 用 `axisNumber` 构造、`DeviceFactory.FindDrives` 自动识别多轴。

## 验证方式（改完主站必跑）

```bash
dotnet build
dotnet run --project src/EtherCAT.Demo                       # 控制台端到端冒烟（仿真）
dotnet run --project src/EtherCAT.App -- --selftest          # 界面自检，结果写入 selftest.log
```
判定标准：WKC 与期望值一致且错误计数为 0；伺服能进入 Operation Enabled；IO 写出读回一致。

## 关键协议事实（已核实，别再反复查）

- 自动增量寻址：主站第 n 站 ADP = -(n)（第 1 站 0xFFFF）；从站先 +1 再判断是否等于 0。
- SII 固定区用**字地址**：vendor=8、product=0x0A、rev=0x0C、serial=0x0E、Rx 邮箱=0x18、Tx 邮箱=0x1A、协议=0x1C；
  类别区起始字 0x40（字节 0x80），SOEM 的 `siifind` 返回长度字的字节地址，数据从 +2 开始。
- CoE：邮箱头 6B（Length/Address/Priority/Type+Count，Type 在低 4 位、Count 在高 4 位）+ CoE 头 2B
  （Number 低 9 位 + Service 高 4 位）+ 命令 1B + Index 2B + SubIndex 1B + 数据；上传请求 length=0x0A。
- WKC：读 +1、写 +2，LRW 读写都成功 +3。
- 状态机：AL 控制 0x0120、AL 状态 0x0130、状态码 0x0134；SM 在 0x0800+8n，FMMU 在 0x0600+16n。

## 网络（本机环境）

- raw.githubusercontent.com 被墙，api.github.com 可用：取源码用
  `curl -s https://api.github.com/repos/<owner>/<repo>/contents/<path>` + base64 解码。
- NuGet 走 nuget.azure.cn 镜像（api.nuget.org 会 302 过去）。
