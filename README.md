# EtherCAT 主站（.NET 8 + Avalonia）

一个用 C# 从协议层实现的 EtherCAT 主站，配套 Avalonia 上位机：支持 **ESI 描述文件驱动的节点扫描**、**CiA402 伺服驱动器控制**、**数字量 IO 模块读写**。

工程同时内置一套 **从站仿真链路**（虚拟 ESC + EEPROM/SII + CoE 对象字典），无需真实硬件即可完整跑通
"扫描 → 匹配 ESI → 建立 PDO 映射 → 进入 OP → 周期过程数据 → 控电机/读写 IO" 的全流程。

> 与开源项目的关系：协议细节（ESC 寄存器地址、SII EEPROM 字地址、CoE 邮箱/SDO 报文布局、WKC 计数规则等）
> 以 ETG 规范与 **[SOEM](https://github.com/OpenEtherCATsociety/SOEM)**（GPLv3 / 商业双授权）的公开实现为参考，
> 本工程为**纯托管（managed）实现**，不链接任何第三方原生库，避免 GPL 传染与本机编译依赖；
> 如需直接使用原生 SOEM，只要把 `IEthercatLink` 换成对 `soem.dll` 的 P/Invoke 封装即可（接口已隔离）。

---

## 1. 目录结构

```
EtherCAT/
├─ esi/                         示例 ESI（EtherCATInfo.xml）描述文件
│  ├─ SampleCiA402Servo.xml     CiA402 伺服驱动器
│  └─ SampleDigitalIo.xml       16DI/16DO 模块
├─ src/
│  ├─ EtherCAT.Core/            主站核心（net8.0 类库）
│  │  ├─ Protocol/              EtherCAT 帧/数据报编解码、ESC 寄存器、CoE/邮箱常量
│  │  ├─ Link/
│  │  │  ├─ IEthercatLink.cs    链路抽象（收发原始以太网帧）
│  │  │  ├─ NpcapLink.cs        Npcap/WinPcap 真实网卡链路（P/Invoke wpcap）
│  │  │  └─ Simulated/          仿真链路 + 仿真从站（ESC/SII/CoE/行为模型）
│  │  ├─ Master/
│  │  │  ├─ EthercatMaster.cs   扫描 / 配置 / 状态机 / 周期过程数据
│  │  │  ├─ SiiReader.cs        EEPROM(SII) 解析（含缓存）
│  │  │  ├─ CoeClient.cs        CoE 邮箱 SDO 上传下载
│  │  │  ├─ SlaveInfo.cs        从站信息、PDO 映射与变量绑定
│  │  │  └─ ProcessImage.cs     逻辑过程数据镜像
│  │  ├─ Esi/                   ESI 描述文件解析与数据库
│  │  └─ Devices/               CiA402 驱动器、IO 模块、设备识别工厂
│  ├─ EtherCAT.App/             Avalonia 11 上位机
│  └─ EtherCAT.Demo/            控制台端到端冒烟测试
└─ README.md
```

## 2. 编译与运行

```bash
dotnet build
dotnet run --project src/EtherCAT.Demo        # 控制台冒烟测试（仿真网络）
dotnet run --project src/EtherCAT.App         # Avalonia 上位机
dotnet run --project src/EtherCAT.App -- --selftest   # 界面逻辑自检，结果写入 selftest.log
```

冒烟测试预期输出（节选）：

```
扫描到 3 个从站，开始读取从站信息…
#1 Sample CiA402 Servo Drive 厂商=0x0000ABCD 产品=0x00000001 … [CoE] [ESI 匹配]
#2 Sample 16DI/16DO Module   厂商=0x0000ABCD 产品=0x00000002 … [CoE] [ESI 匹配]
#3 Sample CiA402 Servo Drive …
网络已进入 OP，期望 WKC = 9
使能后状态：Operation Enabled (0x1637)
位置模式下：位置=500000 目标=500000 到位=True
写入 DO = 0x00FF   读取 DI = 0xA5A5
周期通信：WKC = 9 / 9，错误计数 0
== 冒烟测试通过 ==
```

## 3. 上位机使用

1. 启动后默认勾中 **仿真模式（虚拟从站）**，点 **连接** → **扫描节点** → **配置并进入 OP** → **启动周期**。
2. **伺服驱动** 页：使能 / 断电 / 故障复位 / 停止，选择运行模式（PP、PV、HM、CSP、CSV），
   设置目标速度或目标位置，Jog 点动、回零，实时显示状态字、位置、速度、转矩、错误码。
3. **IO 模块** 页：DI 实时指示，DO 点击切换，支持全部置位/清零。
4. **PDO 监视** 页：所有 PDO 变量的实时值（支持位级变量）。
5. 真实硬件：取消勾选仿真模式 → 选择网卡（Npcap 设备名）→ 连接。

## 4. 协议实现要点

| 环节 | 实现 |
| --- | --- |
| 帧 | Ethernet II + EtherType `0x88A4`，支持多数据报、自动/配置/广播/逻辑寻址全部命令 |
| 扫描 | 广播读 `0x0000` 按 WKC 计数 → 自动增量写 `0x0010` 分配站地址（0x1000+n）→ 逐站读 SII |
| SII/EEPROM | `0x0500~0x050F` 状态机读（4/8 字节）、固定区身份（字 8/A/C/E）、邮箱（字 18/1A/1C）、类别区（Strings/General/FMMU/SM/TxPDO50/RxPDO51/DC） |
| 邮箱 | 邮箱头 6 字节（Length/Address/Priority/Type+Count），CoE 头 2 字节（Number 9bit + Service 4bit），SDO 快速上传/下载，含中止码解析 |
| PDO 映射 | 优先 **ESI 描述文件** → 其次 **CoE 动态读取**（0x1C12/0x1C13 + 0x16xx/0x1Axx）→ 最后 **SII 类别**；ESI 的 PDO 与从站不一致时自动用 SDO 改写 PDO 分配 |
| 配置 | 编程 SM0/SM1（邮箱）、SM2/SM3（过程数据）与 FMMU0/FMMU1，按位偏移建立逻辑镜像 |
| 状态机 | INIT → PREOP → SAFEOP → OP，逐站轮询 AL 状态（`0x0130`）/状态码（`0x0134`）并翻译中文诊断 |
| 周期数据 | 单帧 LRW 覆盖整个逻辑镜像，零分配快速路径；WKC 期望值 = 输入 +1、输出 +2 |
| 实时性 | 独立高优先级线程 + Stopwatch 对齐（Thread.Sleep + SpinWait），默认 1 ms |
| DC | 可选：按 ESI 的 `AssignActivate`/`CycleTimeSync0` 写 `0x0981`/`0x09A0`（未做延迟测量与漂移补偿） |

## 5. 接入真实硬件

1. **安装 Npcap**（https://npcap.com，勾选 "WinPcap API compatible mode"），以**管理员权限**运行程序。
   `NpcapLink` 会依次尝试 `wpcap.dll`、`System32\Npcap\wpcap.dll`、`System32\wpcap.dll`。
2. 网卡选择 EtherCAT 网段那张**专用有线网卡**（不要走 Wi-Fi/虚拟网卡）。
3. 把从站厂商提供的 `EtherCATInfo.xml` 拷到程序目录的 `esi/` 下（或点"重载 ESI"），
   主站按 **VendorId + ProductCode + RevisionNo** 自动匹配；匹配不到时退化为 CoE 动态映射。
4. 典型现场网络下还需确认：从站 EEPROM 中的别名/地址、DC 配置、从站本身是否已处于 INIT。
5. Windows 不是实时系统，1 ms 及以下周期建议配合 RTX/IntervalZero、或把网卡中断与线程绑核；
   生产环境更推荐 Linux + PREEMPT_RT / IgH EtherCAT Master。

## 6. 已验证 / 未验证

- ✅ 已在**仿真链路**上端到端验证：扫描、SII、CoE SDO、SM/FMMU 配置、状态机、LRW 周期、CiA402 使能/速度/位置模式、IO 读写。
- ⚠️ **未**在真实 EtherCAT 从站上联调。真实硬件首次接入时请重点关注：EEPROM 控制权切换（0x0500）、
  邮箱状态位判定、PDO 分配是否需要先清零 0x1C12/0x1C13、从站要求的 DC/同步模式。
- 未实现：SoE/FoE/EoE、分段 SDO（>4 字节）、CoE 紧急报文、从站在线烧写 EEPROM、冗余与热插拔。

## 7. 二次开发接口

```csharp
// 1) 选择链路
IEthercatLink link = new NpcapLink();            // 真实网卡
// IEthercatLink link = SimulatedLink.CreateFromEsi(esiDevices);  // 仿真

// 2) 加载 ESI 并扫描
var esi = new EsiDatabase();
esi.LoadDirectory("esi");

using var master = new EthercatMaster(link, esi, new DelegateLog(Console.WriteLine));
master.Scan();

// 3) 配置并进入 OP
master.Configure(useDistributedClock: true);

// 4) 周期运行
master.CycleTimeUs = 1000;
master.StartCyclic();

// 5) 直接用 PDO 变量，或用设备层封装
var drive = DeviceFactory.FindDrives(master).First();
drive.Enable();
drive.Mode = CiA402Mode.ProfileVelocity;
drive.SetTargetVelocity(100000);

var io = DeviceFactory.FindIoModules(master).First();
io.SetOutput(0, true);
bool input0 = io.GetInput(0);
```

如果你更习惯"配置先行"的方式，也可以直接用 `master.Image` 的位级读写，或 `slave.Outputs/Inputs`
里按对象字典索引取 `PdoVariable`（`RawValue` / `SignedValue` / `BoolValue`）。
