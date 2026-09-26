using System.Globalization;
using System.Text;

namespace EtherCAT.Link.Simulated;

/// <summary>虚拟数字量输入的生成方式</summary>
public enum VirtualDigitalInputMode
{
    /// <summary>保持外部写入的静态值（由 SetDigitalInput 设置）</summary>
    Static,
    /// <summary>输出回环：DI 跟随 DO，便于验证"写出去再读回来"</summary>
    Loopback,
    /// <summary>二进制计数器递增</summary>
    Counter,
    /// <summary>跑马灯（单 bit 循环移位）</summary>
    Runner,
    /// <summary>伪随机</summary>
    Random,
    /// <summary>方波（全部位同相翻转）</summary>
    Square
}

/// <summary>虚拟模拟量输入波形</summary>
public enum VirtualAnalogWave
{
    Sine,
    Triangle,
    Sawtooth,
    Square,
    Noise,
    Constant
}

/// <summary>虚拟 IO 模块参数</summary>
public sealed class VirtualIoOptions
{
    public int DigitalInputBytes { get; set; } = 8;      // 8 字节 = 64 点
    public int DigitalOutputBytes { get; set; } = 8;
    public int AnalogInputCount { get; set; } = 8;
    public int AnalogOutputCount { get; set; } = 4;

    public VirtualDigitalInputMode InputMode { get; set; } = VirtualDigitalInputMode.Loopback;
    public double InputPeriod { get; set; } = 0.5;

    public VirtualAnalogWave Wave { get; set; } = VirtualAnalogWave.Sine;
    public double FrequencyHz { get; set; } = 0.5;
    public double AmplitudeVolts { get; set; } = 5.0;
    public double OffsetVolts { get; set; } = 5.0;
    public double NoiseVolts { get; set; } = 0.05;

    /// <summary>满量程电压（±RangeVolts 对应 ±32767）</summary>
    public double RangeVolts { get; set; } = 10.0;
    /// <summary>模拟量输出斜率限制（V/s），0 = 不限制</summary>
    public double SlewRateVoltsPerSecond { get; set; } = 20.0;
    /// <summary>输入滤波（ms），模拟硬件去抖</summary>
    public int DebounceMs { get; set; } = 0;

    public VirtualIoOptions Clone() => (VirtualIoOptions)MemberwiseClone();
}

/// <summary>
/// 虚拟 IO 模块：数字量输入/输出 + 模拟量输入/输出。
/// 输入可按回环、计数、跑马灯、随机、方波等方式自动生成，模拟量可输出波形并带噪声，
/// 模拟量输出带斜率限制，用于在没有真实信号源时验证主站的采集与控制逻辑。
/// </summary>
public sealed class VirtualIoBehavior : ISlaveBehavior
{
    private const ushort DiIndex = 0x6000;
    private const ushort DoIndex = 0x7000;
    private const ushort AiIndex = 0x6401;
    private const ushort AoIndex = 0x6411;

    private readonly VirtualIoOptions _options;
    private readonly byte[] _digitalInputs;
    private readonly byte[] _digitalOutputs;
    private readonly byte[] _stableInputs;
    private readonly double[] _debounceTimers;
    private readonly double[] _analogInputs;
    private readonly double[] _analogOutputTargets;
    private readonly double[] _analogOutputs;
    private readonly Dictionary<int, bool> _forcedInputs = new();
    private readonly Dictionary<int, double> _forcedAnalogInputs = new();
    private readonly Random _random = new(12345);

    private double _time;
    private double _lastEdgeTime;
    private ulong _counter;
    private int _runnerBit = -1;

    public VirtualIoBehavior(VirtualIoOptions? options = null)
    {
        _options = options ?? new VirtualIoOptions();
        _digitalInputs = new byte[Math.Max(1, _options.DigitalInputBytes)];
        _digitalOutputs = new byte[Math.Max(1, _options.DigitalOutputBytes)];
        _stableInputs = new byte[_digitalInputs.Length];
        _debounceTimers = new double[_digitalInputs.Length];
        _analogInputs = new double[Math.Max(0, _options.AnalogInputCount)];
        _analogOutputTargets = new double[Math.Max(0, _options.AnalogOutputCount)];
        _analogOutputs = new double[Math.Max(0, _options.AnalogOutputCount)];
    }

    public VirtualIoOptions Options => _options;

    /// <summary>外部强制某个数字量输入（优先级高于自动波形）</summary>
    public void SetDigitalInput(int channel, bool value) => _forcedInputs[channel] = value;

    public void ClearForcedDigitalInputs() => _forcedInputs.Clear();

    /// <summary>外部设定某个模拟量输入电压（V）</summary>
    public void SetAnalogInput(int channel, double volts) => _forcedAnalogInputs[channel] = volts;

    public void ClearForcedAnalogInputs() => _forcedAnalogInputs.Clear();

    public bool GetDigitalOutput(int channel)
    {
        int byteIndex = channel >> 3;
        if (byteIndex >= _digitalOutputs.Length) return false;
        return (_digitalOutputs[byteIndex] & (1 << (channel & 7))) != 0;
    }

    public bool GetDigitalInput(int channel)
    {
        int byteIndex = channel >> 3;
        if (byteIndex >= _digitalInputs.Length) return false;
        return (_digitalInputs[byteIndex] & (1 << (channel & 7))) != 0;
    }

    public double GetAnalogInputVolts(int channel) =>
        channel >= 0 && channel < _analogInputs.Length ? _analogInputs[channel] : 0;

    public double GetAnalogOutputVolts(int channel) =>
        channel >= 0 && channel < _analogOutputs.Length ? _analogOutputs[channel] : 0;

    /// <summary>切换输入生成模式</summary>
    public void SetInputMode(VirtualDigitalInputMode mode)
    {
        _options.InputMode = mode;
        _counter = 0;
        _runnerBit = -1;
    }

    // ------------------------------------------------------------ ISlaveBehavior

    public void OnOutputsWritten(SimulatedSlave slave, double dtSeconds)
    {
        for (int i = 0; i < _digitalOutputs.Length; i++)
            _digitalOutputs[i] = (byte)slave.GetObjectValue(DoIndex, (byte)(i + 1));

        for (int i = 0; i < _analogOutputTargets.Length; i++)
        {
            int raw = (short)(ushort)slave.GetObjectValue(AoIndex, (byte)(i + 1));
            _analogOutputTargets[i] = RawToVolts(raw);
        }
    }

    public void Simulate(SimulatedSlave slave, double dtSeconds)
    {
        double dt = dtSeconds > 0 ? Math.Min(dtSeconds, 0.05) : 0.001;
        _time += dt;

        // 模拟量输出斜率限制
        for (int i = 0; i < _analogOutputs.Length; i++)
        {
            double target = _analogOutputTargets[i];
            if (_options.SlewRateVoltsPerSecond <= 0)
            {
                _analogOutputs[i] = target;
                continue;
            }
            double step = _options.SlewRateVoltsPerSecond * dt;
            double delta = target - _analogOutputs[i];
            _analogOutputs[i] = Math.Abs(delta) <= step ? target : _analogOutputs[i] + Math.Sign(delta) * step;
        }

        GenerateDigitalInputs(dt);
        GenerateAnalogInputs();
    }

    public void BeforeInputsRead(SimulatedSlave slave, double dtSeconds)
    {
        for (int i = 0; i < _digitalInputs.Length; i++)
            slave.SetObjectValue(DiIndex, (byte)(i + 1), _digitalInputs[i], 1);

        for (int i = 0; i < _analogInputs.Length; i++)
        {
            double volts = _forcedAnalogInputs.TryGetValue(i, out double forced)
                ? forced
                : _analogInputs[i];
            slave.SetObjectValue(AiIndex, (byte)(i + 1), unchecked((ulong)(ushort)VoltsToRaw(volts)), 2);
        }

        // 模拟量输出回读：把斜率限制后的实际值写回对象，便于主站监视
        for (int i = 0; i < _analogOutputs.Length; i++)
            slave.SetObjectValue(AoIndex, (byte)(i + 1), unchecked((ulong)(ushort)VoltsToRaw(_analogOutputs[i])), 2);
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"DI={Convert.ToHexString(_digitalInputs)} ");
        sb.Append(CultureInfo.InvariantCulture, $"DO={Convert.ToHexString(_digitalOutputs)}");
        if (_analogInputs.Length > 0)
            sb.Append(CultureInfo.InvariantCulture, $" AI[0]={_analogInputs[0]:F2}V");
        return sb.ToString();
    }

    // ------------------------------------------------------------ 输入生成

    private void GenerateDigitalInputs(double dt)
    {
        switch (_options.InputMode)
        {
            case VirtualDigitalInputMode.Loopback:
                for (int i = 0; i < _digitalInputs.Length; i++)
                    _digitalInputs[i] = i < _digitalOutputs.Length ? _digitalOutputs[i] : (byte)0;
                break;

            case VirtualDigitalInputMode.Counter:
                if (_time - _lastEdgeTime >= _options.InputPeriod)
                {
                    _lastEdgeTime = _time;
                    _counter++;
                    var bytes = BitConverter.GetBytes(_counter);
                    for (int i = 0; i < _digitalInputs.Length; i++)
                        _digitalInputs[i] = bytes[i % bytes.Length];
                }
                break;

            case VirtualDigitalInputMode.Runner:
                if (_time - _lastEdgeTime >= _options.InputPeriod)
                {
                    _lastEdgeTime = _time;
                    _runnerBit = (_runnerBit + 1) % (_digitalInputs.Length * 8);
                    for (int i = 0; i < _digitalInputs.Length; i++) _digitalInputs[i] = 0;
                    _digitalInputs[_runnerBit >> 3] |= (byte)(1 << (_runnerBit & 7));
                }
                break;

            case VirtualDigitalInputMode.Random:
                if (_time - _lastEdgeTime >= _options.InputPeriod)
                {
                    _lastEdgeTime = _time;
                    _random.NextBytes(_digitalInputs);
                }
                break;

            case VirtualDigitalInputMode.Square:
                if (_time - _lastEdgeTime >= _options.InputPeriod)
                {
                    _lastEdgeTime = _time;
                    byte value = _digitalInputs[0] == 0 ? (byte)0xFF : (byte)0x00;
                    for (int i = 0; i < _digitalInputs.Length; i++) _digitalInputs[i] = value;
                }
                break;

            default:   // Static：保留外部强制/上一次的值
                break;
        }

        // 通道级强制
        foreach (var pair in _forcedInputs)
        {
            int byteIndex = pair.Key >> 3;
            if (byteIndex >= _digitalInputs.Length) continue;
            int mask = 1 << (pair.Key & 7);
            if (pair.Value) _digitalInputs[byteIndex] |= (byte)mask;
            else _digitalInputs[byteIndex] &= (byte)~mask;
        }

        // 硬件去抖
        if (_options.DebounceMs > 0)
        {
            double threshold = _options.DebounceMs / 1000.0;
            for (int i = 0; i < _digitalInputs.Length; i++)
            {
                if (_digitalInputs[i] == _stableInputs[i])
                {
                    _debounceTimers[i] = 0;
                    continue;
                }
                _debounceTimers[i] += dt;
                if (_debounceTimers[i] >= threshold)
                {
                    _stableInputs[i] = _digitalInputs[i];
                    _debounceTimers[i] = 0;
                }
                else
                {
                    _digitalInputs[i] = _stableInputs[i];
                }
            }
        }
    }

    private void GenerateAnalogInputs()
    {
        for (int i = 0; i < _analogInputs.Length; i++)
        {
            // 各通道相位错开，便于观察多通道采集
            double phase = 2 * Math.PI * _options.FrequencyHz * _time + i * (2 * Math.PI / Math.Max(1, _analogInputs.Length));
            double value = _options.Wave switch
            {
                VirtualAnalogWave.Sine => _options.OffsetVolts + _options.AmplitudeVolts * Math.Sin(phase),
                VirtualAnalogWave.Triangle => _options.OffsetVolts + _options.AmplitudeVolts * Triangle(phase),
                VirtualAnalogWave.Sawtooth => _options.OffsetVolts + _options.AmplitudeVolts * Sawtooth(phase),
                VirtualAnalogWave.Square => _options.OffsetVolts + _options.AmplitudeVolts * (Math.Sin(phase) >= 0 ? 1 : -1),
                VirtualAnalogWave.Noise => _options.OffsetVolts + (_random.NextDouble() * 2 - 1) * _options.AmplitudeVolts,
                _ => _options.OffsetVolts
            };

            if (_options.NoiseVolts > 0 && _options.Wave != VirtualAnalogWave.Noise)
                value += (_random.NextDouble() * 2 - 1) * _options.NoiseVolts;

            _analogInputs[i] = BehaviorMath.Clamp(value, -_options.RangeVolts, _options.RangeVolts);
        }
    }

    private static double Triangle(double phase)
    {
        double t = Normalize(phase);
        return t < 0.25 ? t * 4 : t < 0.75 ? 2 - t * 4 : t * 4 - 4;
    }

    private static double Sawtooth(double phase) => Normalize(phase) * 2 - 1;

    private static double Normalize(double phase)
    {
        double t = phase / (2 * Math.PI);
        return t - Math.Floor(t);
    }

    private double RawToVolts(int raw) => raw / 32767.0 * _options.RangeVolts;

    private int VoltsToRaw(double volts)
    {
        double clamped = BehaviorMath.Clamp(volts, -_options.RangeVolts, _options.RangeVolts);
        return (int)Math.Round(clamped / _options.RangeVolts * 32767);
    }
}
