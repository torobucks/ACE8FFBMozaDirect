// MozaSdk.cs — thin C# binding to the official MOZA SDK (MOZA_SDK.dll) plus the
// direct force-feedback driver for the AB6/AB9 flight base.
//
// This REPLACES the old two-stage pipeline (fake-DCS telemetry over TCP -> MOZA
// Cockpit -> base) with one direct hop: telemetry in from the AC8 mod, force out
// straight to the base over the SDK's native FFB commands (MainCtrl_Ffb*).
//
// Why the SDK and not DirectInput: the SDK is MOZA's supported, first-party path
// and works regardless of the base's DirectInput/Telemetry mode. It needs only
// MOZA's DeviceService running (shipped with MOZA Cockpit). DCS is never involved.
//
// Command/parameter IDs are the stable constants from the 1.0.0.6 generated
// header (device_as.generated.hpp). Raw (untyped) command arguments are sent as a
// flat MOZA_VALUE list in the exact reflected field order; typed C++ flattens the
// same order, so raw lists are byte-compatible.
using System;
using System.Runtime.InteropServices;
using System.Text;

// ---- C ABI: MOZA_SDK.dll (exported with extern "C", so names are undecorated) ----
internal static class MozaNative
{
    public const int VT_INVALID = 0, VT_NULL = 1, VT_INT = 2, VT_UINT = 3, VT_DOUBLE = 4,
                     VT_BOOL = 5, VT_STRING = 6, VT_BINARY = 7, VT_LIST = 8;

    // Categories / types (from MOZA_SDK_API 参考手册 / moza.h)
    public const int DC_UNKNOWN = 0, DC_RS = 1, DC_AS = 2;
    public const int DT_AS_FLIGHTBASE = 1 << 0;

    // Flight-base FFB commands (device_as.generated.hpp)
    public const int CMD_NEW_EFFECT   = 603; // NewEffect(type, index)
    public const int CMD_CONSTANT     = 604; // (avoided: enableAxis is a struct in raw mode)
    public const int CMD_PERIODIC     = 606; // Periodic(effectIndex, mag, offset, phase, period, dur, enableAxis, dirX, dirY)
    public const int CMD_CONDITION    = 607; // Condition(effectIndex, paramBlockOffset, center, posC, negC, posSat, negSat, deadBand, dur, enableAxis, dirX, dirY)
    public const int CMD_EFFECT_OP    = 609; // EffectOperation(effectIndex, op)
    public const int CMD_CONTROL      = 610; // Control(control)

    // Effect types
    public const int FX_CONSTANT = 1, FX_SQUARE = 3, FX_SINE = 4, FX_SPRING = 8,
                     FX_DAMPER = 9, FX_INERTIA = 10, FX_FRICTION = 11;

    // Effect operations
    public const int OP_STOP = 0, OP_START = 1, OP_FREE = 2;
    // FFB control
    public const int CTRL_STOP = 0, CTRL_START = 1, CTRL_RESET = 2, CTRL_STOP_ALL = 3;

    // Enable-axis bitmask for Condition / Periodic: bit0 = X, bit1 = Y, bit2 = DirectionEnable
    public const int ENABLE_XY_DIR = 0b111;

    // Parameters
    public const int PARAM_FFB_ENABLE = 312; // Main_CtrlFfbEnable (0 = FFB disabled)

    // Error codes (subset; full list in MOZA_SDK_API 参考手册)
    public const int ERR_NO_ERROR = 0, ERR_SERVICE_NOT_FOUND = 2, ERR_INVALID_ARGUMENT = 4,
                     ERR_NOT_INITIALIZED = 5, ERR_BUFFER_TOO_SMALL = 7, ERR_DEVICE_NOT_FOUND = 8,
                     ERR_INVALID_HANDLE = 10, ERR_BUSY = 11, ERR_TIMEOUT = 12;

    [DllImport("MOZA_SDK.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Moza_Initialize();
    [DllImport("MOZA_SDK.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Moza_Shutdown();
    [DllImport("MOZA_SDK.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Moza_DeviceGetList([In, Out] MOZA_DEVICE_INFO[] infos, ref uint infosSize);
    [DllImport("MOZA_SDK.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Moza_DeviceOpen(uint deviceId, out IntPtr handle);
    [DllImport("MOZA_SDK.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Moza_DeviceClose(IntPtr handle);
    [DllImport("MOZA_SDK.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Moza_DeviceParameterGetValueSync(IntPtr handle, int parameterId, int timeoutMs, ref MOZA_VALUE value);
    [DllImport("MOZA_SDK.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Moza_DeviceCommandSendSync(IntPtr handle, int commandId, ref MOZA_VALUE arguments, int timeoutMs, ref MOZA_VALUE resultValue);
    [DllImport("MOZA_SDK.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Moza_DeviceCommandSendSync(IntPtr handle, int commandId, IntPtr arguments, int timeoutMs, IntPtr resultValue);
    [DllImport("MOZA_SDK.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern byte Moza_ValueCreate(int type, out MOZA_VALUE value, uint size);
    [DllImport("MOZA_SDK.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Moza_ValueDestroy(ref MOZA_VALUE value);
    [DllImport("MOZA_SDK.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Moza_SetLogCallback(MozaLogCallback callback, IntPtr userData);

    // ---- SDK log capture (mirrored into the GUI log panel) ----
    // MOZA_LOG_CONTEXT layout from moza.h: level, message*, messageSize,
    // category*, categorySize, reserved[4] (pointers are 64-bit here).
    [StructLayout(LayoutKind.Sequential)]
    public struct MOZA_LOG_CONTEXT
    {
        public int level;
        public IntPtr message;
        public uint messageSize;
        public IntPtr category;
        public uint categorySize;
        public IntPtr reserved0, reserved1, reserved2, reserved3;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MozaLogCallback(ref MOZA_LOG_CONTEXT context, IntPtr userData);

    /// <summary>Set by Program when the GUI is active; each SDK log line is pushed here.</summary>
    public static Action<string> LogSink;
    static MozaLogCallback sdkLogCb;   // rooted: must survive for the SDK's lifetime
    static bool sdkLogInstalled;

    // Install the SDK's log callback (idempotent). Buffers are borrowed and not
    // null-terminated, so we copy exactly the documented byte counts immediately.
    public static void InstallSdkLog()
    {
        if (sdkLogInstalled) return;
        sdkLogInstalled = true;
        sdkLogCb = (ref MOZA_LOG_CONTEXT c, IntPtr _) =>
        {
            if (LogSink == null) return;
            // Buffers are borrowed and NOT null-terminated: copy the documented byte
            // counts exactly (bounded defensively), then decode as UTF-8.
            int mlen = (int)Math.Min(c.messageSize, 8192u);
            var mb = new byte[mlen];
            if (c.message != IntPtr.Zero && mlen > 0) Marshal.Copy(c.message, mb, 0, mlen);
            int clen = (int)Math.Min(c.categorySize, 128u);
            var cb = new byte[clen];
            if (c.category != IntPtr.Zero && clen > 0) Marshal.Copy(c.category, cb, 0, clen);
            string msg = Encoding.UTF8.GetString(mb);
            string cat = Encoding.UTF8.GetString(cb);
            string lvl = c.level switch { 0 => "DEBUG", 1 => "INFO", 2 => "WARN", 3 => "CRIT", _ => "FATAL" };
            LogSink($"[sdk-{lvl}] {(string.IsNullOrEmpty(cat) ? "moza" : cat)}: {msg}\n");
        };
        try { Moza_SetLogCallback(sdkLogCb, IntPtr.Zero); }
        catch { sdkLogInstalled = false; }
    }
}

// MOZA_VALUE: C variant container. type is an int, then a 12-byte union.
// Total size 16 bytes (pack 1). We only ever fill scalar (INT/UINT) items and
// read scalar replies, so we marshal the union as raw bytes at the right offsets.
[StructLayout(LayoutKind.Explicit, Pack = 1)]
internal struct MOZA_VALUE
{
    [FieldOffset(0)] public int type;
    [FieldOffset(4)] public long intValue;
    [FieldOffset(4)] public ulong uintValue;
    [FieldOffset(4)] public double doubleValue;
    [FieldOffset(4)] public IntPtr data;   // string/binary/list payload pointer
    [FieldOffset(12)] public uint size;    // string/binary/list length
    public static readonly int Size = Marshal.SizeOf(typeof(MOZA_VALUE));
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct MOZA_DEVICE_INFO
{
    public uint id;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string name;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string portName;
    public ushort vendorId;
    public ushort productId;
    public int category;
    public int type;
    public int state;
    public byte hidDeviceCount;
    public IntPtr reserved;
}

// ---- Direct FFB driver on the MOZA flight base ----
class MozaFfb
{
    public bool Ok;
    public double Scale = 1.0;  // --vib : rumble scale
    public double Gain = 1.0;   // --gain: master strength for every force
    public volatile string CurrentProfile = "";  // last aircraft profile applied (for the GUI)

    // Registered effect indices (1..80 allowed; we use a small fixed set)
    const int EFF_SPRING = 1, EFF_DAMPER = 2, EFF_FRICTION = 3, EFF_INERTIA = 4, EFF_SINE = 5, EFF_CONSTANT = 6;
    // Effect duration in ms. The MOZA base treats duration literally (unit ms); sending
    // duration=0 was found to play NO force (effect ends immediately). Keep every effect
    // alive with a long hold (uint16 max) and re-send it each frame, as the SDK guide's
    // "periodically send params" model describes.
    const int DUR_HOLD = 65535;

    IntPtr handle;
    bool initialized;
    int lastSpring = -1, lastDamper = -1, lastFriction = -1, lastInertia = -1;
    int lastMag = -1, lastPeriod = -1; DateTime lastUpdate = DateTime.MinValue;
    string lastProfile = "";
    double evMag, evPeriodMs; DateTime evUntil = DateTime.MinValue;
    const int TIMEOUT = 300;

    public MozaFfb(double vibScale, double gain)
    {
        Scale = vibScale; Gain = gain;
        try
        {
            // Capture the SDK's own INFO/DEBUG/WARN log lines (mirrored into the GUI
            // log panel when one is attached). Must be installed BEFORE Moza_Initialize
            // so the very first SDK startup messages are not missed.
            if (MozaNative.LogSink != null) MozaNative.InstallSdkLog();
            int rc = MozaNative.Moza_Initialize();
            if (rc != 0) { Console.WriteLine($"[ffb] Moza_Initialize failed ({rc}). Is MOZA DeviceService running?"); return; }
            initialized = true;

            // Discover the flight base. Discovery is ASYNCHRONOUS: a freshly-started
            // DeviceService takes a moment to report the base. The official example
            // polls the list (it waits up to 2 s); we allow ~5 s for slower service
            // startup, then load the list. Querying once and giving up misses the base.
            uint n = 0; int pollMs = 0;
            for (int attempt = 0; attempt < 35; attempt++)
            {
                n = 0;
                int rc0 = MozaNative.Moza_DeviceGetList(null, ref n);
                if (rc0 != 0 && attempt == 0) Console.WriteLine($"[ffb] Moza_DeviceGetList(count) rc={rc0}");
                if (n > 0) break;
                System.Threading.Thread.Sleep(150); pollMs += 150;
            }
            if (n == 0)
            {
                Console.WriteLine("[ffb] no MOZA devices found after polling ~5 s (service up, SDK reports 0 devices).");
                Console.WriteLine("[ffb]   Check the AB6/AB9 is powered and plugged in, and that MOZA Cockpit's device page shows it.");
                return;
            }
            if (pollMs > 0) Console.WriteLine($"[ffb] base appeared after ~{pollMs} ms of polling");
            // Load the list; it can grow between the count and data query (retry on BUFFER_TOO_SMALL).
            MOZA_DEVICE_INFO[] infos = null; uint got = n;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                infos = new MOZA_DEVICE_INFO[got]; uint cap = got;
                rc = MozaNative.Moza_DeviceGetList(infos, ref cap);
                if (rc == 0) { got = cap; break; }
                if (rc == MozaNative.ERR_BUFFER_TOO_SMALL) { uint q = 0; MozaNative.Moza_DeviceGetList(null, ref q); got = Math.Max(q, got) + 1; continue; }
                Console.WriteLine($"[ffb] device list failed ({rc})"); return;
            }
            uint found = 0; string foundName = "";
            for (uint i = 0; i < got; i++)
            {
                var d = infos[i];
                Console.WriteLine($"[ffb] device[{i}] id={d.id} name=\"{d.name}\" cat={d.category} type={d.type} state={d.state} vid={d.vendorId:X4} pid={d.productId:X4}");
                if (d.category == MozaNative.DC_AS && (d.type & MozaNative.DT_AS_FLIGHTBASE) != 0)
                { found = d.id; foundName = d.name; }
            }
            if (found == 0)
            {
                Console.WriteLine("[ffb] devices were listed but none matched a flight base (category/type above).");
                Console.WriteLine("[ffb]   If your base appears there with a different cat/type, report these lines so the filter can be widened.");
                return;
            }

            rc = MozaNative.Moza_DeviceOpen(found, out handle);
            if (rc != 0) { Console.WriteLine($"[ffb] open {foundName} failed ({rc})"); handle = IntPtr.Zero; return; }

            // Sanity check: FFB must be enabled on the base (Main_CtrlFfbEnable).
            var v = new MOZA_VALUE();
            int er = MozaNative.Moza_DeviceParameterGetValueSync(handle, MozaNative.PARAM_FFB_ENABLE, TIMEOUT, ref v);
            if (er == 0 && v.type == MozaNative.VT_UINT)
                Console.WriteLine(v.uintValue != 0
                    ? "[ffb] Main_CtrlFfbEnable = 1 (base FFB enabled)"
                    : "[ffb] WARNING: Main_CtrlFfbEnable = 0 on the base - force feedback is DISABLED. Enable 'Force Feedback' in MOZA Cockpit (or set Spring/Gain up), or no forces will reach the stick.");
            else Console.WriteLine($"[ffb] note: could not read Main_CtrlFfbEnable (rc={er}); assuming FFB is enabled.");

            // Reset + start the FFB block, then register effects. Order follows the
            // SDK's official example: register -> send an initial parameter block ->
            // then EffectOperation Start (starting before any params can yield nothing).
            Control(MozaNative.CTRL_RESET);
            Control(MozaNative.CTRL_START);
            NewEffect(MozaNative.FX_SPRING,   EFF_SPRING);
            NewEffect(MozaNative.FX_DAMPER,   EFF_DAMPER);
            NewEffect(MozaNative.FX_FRICTION, EFF_FRICTION);
            NewEffect(MozaNative.FX_INERTIA,  EFF_INERTIA);
            NewEffect(MozaNative.FX_SINE,     EFF_SINE);
            NewEffect(MozaNative.FX_CONSTANT, EFF_CONSTANT); // diagnostic bench effect
            SetCondition(EFF_SPRING, 0, 0, 0, 10000, 10000, 0, DUR_HOLD);
            SetCondition(EFF_DAMPER, 0, 0, 0, 10000, 10000, 0, DUR_HOLD);
            SetCondition(EFF_FRICTION, 0, 0, 0, 10000, 10000, 0, DUR_HOLD);
            SetCondition(EFF_INERTIA, 0, 0, 0, 10000, 10000, 0, DUR_HOLD);
            SetSine(0, 50, DUR_HOLD);
            SetConstant(EFF_CONSTANT, 0, DUR_HOLD);
            EffectOp(EFF_SPRING, MozaNative.OP_START);
            EffectOp(EFF_DAMPER, MozaNative.OP_START);
            EffectOp(EFF_FRICTION, MozaNative.OP_START);
            EffectOp(EFF_INERTIA, MozaNative.OP_START);
            EffectOp(EFF_SINE, MozaNative.OP_START);
            EffectOp(EFF_CONSTANT, MozaNative.OP_START);

            Ok = true;
            Console.WriteLine($"[ffb] MOZA SDK FFB ready on \"{foundName}\" | vib {Scale:0.00}, gain {Gain:0.00}");
            Console.WriteLine("[ffb] effects: spring(1) damper(2) friction(3) inertia(4) sine(5) constant(6)");
        }
        catch (Exception ex) { Console.WriteLine("[ffb] init failed: " + ex.Message); }
    }

    // ---- command plumbing ----

    static MOZA_VALUE IntVal(long v) => new MOZA_VALUE { type = MozaNative.VT_INT, intValue = v };
    static MOZA_VALUE UIntVal(ulong v) => new MOZA_VALUE { type = MozaNative.VT_UINT, uintValue = v };

    // Send a command with a flat argument list. Each arg carries its intended
    // C++ field type: signed fields as INT, unsigned fields as UINT.
    int Send(int cmd, params (int vt, long val)[] args)
    {
        if (handle == IntPtr.Zero) return MozaNative.ERR_INVALID_HANDLE;
        if (args.Length == 0)
            return MozaNative.Moza_DeviceCommandSendSync(handle, cmd, IntPtr.Zero, TIMEOUT, IntPtr.Zero);
        MOZA_VALUE list = new MOZA_VALUE();
        byte okc = MozaNative.Moza_ValueCreate(MozaNative.VT_LIST, out list, (uint)args.Length);
        if (okc == 0) { Console.WriteLine("[ffb] arg list alloc failed"); return -1; }
        try
        {
            IntPtr items = list.data;   // MOZA_VALUE* items
            for (int i = 0; i < args.Length; i++)
            {
                IntPtr it = IntPtr.Add(items, i * MOZA_VALUE.Size);
                Marshal.WriteInt32(it, 0, args[i].vt);
                Marshal.WriteInt64(it, 4, args[i].val);
            }
            MOZA_VALUE result = new MOZA_VALUE();
            return MozaNative.Moza_DeviceCommandSendSync(handle, cmd, ref list, TIMEOUT, ref result);
        }
        finally { MozaNative.Moza_ValueDestroy(ref list); }
    }

    // ---- effect helpers ----
    int NewEffect(int type, int index)
    {
        int rc = Send(MozaNative.CMD_NEW_EFFECT, (MozaNative.VT_UINT, type), (MozaNative.VT_UINT, index));
        if (rc != 0) Console.WriteLine($"[ffb] NewEffect(type={type}, index={index}) rc={rc}");
        return rc;
    }
    void EffectOp(int index, int op)
    {
        int rc = Send(MozaNative.CMD_EFFECT_OP, (MozaNative.VT_UINT, index), (MozaNative.VT_UINT, op));
        if (rc != 0) Console.WriteLine($"[ffb] EffectOp({index},{op}) rc={rc}");
    }
    void Control(int c)
    {
        int rc = Send(MozaNative.CMD_CONTROL, (MozaNative.VT_UINT, c));
        if (rc != 0) Console.WriteLine($"[ffb] Control({c}) rc={rc}");
    }

    // Update a condition effect (spring/damper/friction/inertia).
    // coeff scale: 0..10000 = 0..100% of the base's effect strength.
    void SetCondition(int index, long center, long posC, long negC, long posSat, long negSat, long deadBand, long durMs)
    {
        Send(MozaNative.CMD_CONDITION,
            (MozaNative.VT_UINT, index),   // effectBlockIndex
            (MozaNative.VT_UINT, 0),       // parameterBlockOffset
            (MozaNative.VT_INT, center),   // centerPointOffset (signed)
            (MozaNative.VT_INT, posC),     // positiveCoefficient
            (MozaNative.VT_INT, negC),     // negativeCoefficient
            (MozaNative.VT_UINT, posSat),  // positiveSaturation
            (MozaNative.VT_UINT, negSat),  // negativeSaturation
            (MozaNative.VT_UINT, deadBand),// deadBand
            (MozaNative.VT_UINT, durMs),   // duration (0 = continuous)
            (MozaNative.VT_UINT, MozaNative.ENABLE_XY_DIR), // enableAxis X|Y|DirectionEnable
            (MozaNative.VT_UINT, 0),       // directionX
            (MozaNative.VT_UINT, 0));      // directionY
    }

    // Periodic (sine) rumble. periodMs is in MILLISECONDS: the MOZA base interprets
    // this field natively in ms (sending DirectInput-style microseconds made a
    // 50 s / 28 s cycle, i.e. no vibration at all — confirmed by the user's bench).
    void SetSine(int mag, int periodMs, int durMs)
    {
        Send(MozaNative.CMD_PERIODIC,
            (MozaNative.VT_UINT, EFF_SINE), // effectBlockIndex
            (MozaNative.VT_INT, mag),       // magnitude
            (MozaNative.VT_INT, 0),         // offset
            (MozaNative.VT_UINT, 0),        // phase
            (MozaNative.VT_UINT, (long)periodMs), // period (ms)
            (MozaNative.VT_UINT, (long)durMs),    // duration (ms)
            (MozaNative.VT_UINT, MozaNative.ENABLE_XY_DIR), // enableAxis
            (MozaNative.VT_UINT, 0),        // directionX
            (MozaNative.VT_UINT, 0));       // directionY
    }

    // Constant-force diagnostic (the exact pattern from the SDK's official working
    // example). enableAxis is a 3-int struct; flattened as field0(X), field1(Y),
    // field2(DirectionEnable). Official example: X=0, Y=1, DirectionEnable=1.
    void SetConstant(int index, int magnitude, int durMs)
    {
        Send(MozaNative.CMD_CONSTANT,
            (MozaNative.VT_UINT, index),      // effectBlockIndex
            (MozaNative.VT_INT, magnitude),   // magnitude
            (MozaNative.VT_UINT, (long)durMs),// duration (ms)
            (MozaNative.VT_INT, 0),           // enableAxis.field0 = X
            (MozaNative.VT_INT, 1),           // enableAxis.field1 = Y
            (MozaNative.VT_INT, 1),           // enableAxis.field2 = DirectionEnable
            (MozaNative.VT_UINT, 0),          // directionX
            (MozaNative.VT_UINT, 0));         // directionY
    }

    // ---- public force model ----

    // One-shot event (hit, sonic boom). Stronger events override weaker ones.
    public void Pulse(double mag, double periodMs, double ms)
    {
        if (DateTime.Now < evUntil && mag < evMag) return;
        evMag = mag; evPeriodMs = periodMs; evUntil = DateTime.Now.AddMilliseconds(ms);
    }

    // Called every frame with the plane's profile, smoothed G-load, airspeed (m/s)
    // and continuous rumble levels (gun/cloud/ice/stall, each 0..1).
    public void Update(FfbProfile pr, double g, double tas, double gun, double cloud, double ice, double stall = 0)
    {
        if (!Ok || pr == null || handle == IntPtr.Zero) return;
        try
        {
            if (pr.Name != lastProfile) { lastProfile = pr.Name; lastFriction = -1; lastInertia = -1; }
            CurrentProfile = pr.Name;

            double gFactor = Math.Clamp(g, -2, 4);
            double gLoad   = Math.Clamp(1.0 + (gFactor - 1.0) * pr.GLoad, 0.30, 2.2);
            double spd     = Math.Clamp(tas / 170.0, 0.30, 1.5);
            double spdLoad = Math.Clamp(1.0 + (spd - 1.0) * pr.SpeedLoad, 0.30, 1.8);

            // 0..10000 = 0..100% strength (coeff scale). Duration must be a finite
            // hold (DUR_HOLD) refreshed every frame, or the effect ends instantly.
            double spring01 = Math.Clamp(pr.Spring / 100.0 * gLoad * spdLoad, 0, 1) * pr.Gain * Gain;
            double damper01 = Math.Clamp(pr.Damper / 100.0 * Math.Clamp(1.0 + (gFactor - 1.0) * 0.35, 0.6, 1.8), 0, 1) * pr.Gain * Gain;
            double fric01 = pr.Friction / 100.0 * pr.Gain * Gain;
            double iner01 = pr.Inertia / 100.0 * pr.Gain * Gain;

            int isp = (int)(spring01 * 10000), idm = (int)(damper01 * 10000), ifr = (int)(fric01 * 10000), iin = (int)(iner01 * 10000);
            // Re-send every frame so the effect never expires.
            SetCondition(EFF_SPRING,   0, isp, isp, 10000, 10000, 0, DUR_HOLD);
            SetCondition(EFF_DAMPER,   0, idm, idm, 10000, 10000, 0, DUR_HOLD);
            SetCondition(EFF_FRICTION, 0, ifr, ifr, 10000, 10000, 0, DUR_HOLD);
            SetCondition(EFF_INERTIA,  0, iin, iin, 10000, 10000, 0, DUR_HOLD);

            // Rumble (sine): strongest continuous source wins; one-shot events override.
            double mag = 0, periodMs = 50;
            void Take(double m, double per) { if (m > mag) { mag = m; periodMs = per; } }
            Take(gun * 0.30, 28);
            Take(cloud * 0.22, 70);
            Take(ice * 0.38, 90);
            Take(stall * 0.42, 110);
            if (DateTime.Now < evUntil) Take(evMag, evPeriodMs);
            int imag = (int)Math.Clamp(mag * (pr.Rumble / 100.0) * Scale * 10000, 0, 10000);
            int iper = (int)periodMs;   // period in milliseconds (MOZA native unit)
            SetSine(imag, iper, DUR_HOLD);
        }
        catch (Exception ex) { Console.WriteLine("[ffb] update failed: " + ex.Message); }
    }

    // No aircraft (menu / loading): gentle centering spring, no rumble.
    public void Idle()
    {
        if (!Ok || handle == IntPtr.Zero) return;
        try
        {
            SetCondition(EFF_SPRING, 0, 800, 800, 10000, 10000, 0, DUR_HOLD);
            SetCondition(EFF_DAMPER, 0, 500, 500, 10000, 10000, 0, DUR_HOLD);
            SetCondition(EFF_FRICTION, 0, 0, 0, 10000, 10000, 0, DUR_HOLD);
            SetCondition(EFF_INERTIA, 0, 0, 0, 10000, 10000, 0, DUR_HOLD);
            SetSine(0, 50, DUR_HOLD);
        }
        catch { }
    }

    // Bench/self-test: drive each effect at a fixed level so the user can verify
    // wiring and tune units/scale on their actual base. --test
    public void Test()
    {
        if (!Ok || handle == IntPtr.Zero) return;
        Console.WriteLine("[ffb] bench: CONSTANT push 80% (3s) [official pattern], spring 80%, damper 80%, friction 80%, inertia 80%, sine 8000 @50ms (3s), sine 8000 @5ms (3s), then idle");
        // Phase 0: constant-force push — the exact pattern from the SDK's official
        // example (enableAxis X=0, Y=1, DirectionEnable=1). If the stick DOESN'T move
        // here, the base isn't outputting SDK force at all (Cockpit/base gate), and
        // the rest of the bench is moot.
        SetConstant(EFF_CONSTANT, 8000, DUR_HOLD); sleep(3000);
        SetConstant(EFF_CONSTANT, 0, DUR_HOLD);
        SetCondition(EFF_SPRING, 0, 8000, 8000, 10000, 10000, 0, DUR_HOLD); sleep(3000);
        SetCondition(EFF_SPRING, 0, 0, 0, 10000, 10000, 0, DUR_HOLD);
        SetCondition(EFF_DAMPER, 0, 8000, 8000, 10000, 10000, 0, DUR_HOLD); sleep(3000);
        SetCondition(EFF_DAMPER, 0, 0, 0, 10000, 10000, 0, DUR_HOLD);
        SetCondition(EFF_FRICTION, 0, 8000, 8000, 10000, 10000, 0, DUR_HOLD); sleep(3000);
        SetCondition(EFF_FRICTION, 0, 0, 0, 10000, 10000, 0, DUR_HOLD);
        SetCondition(EFF_INERTIA, 0, 8000, 8000, 10000, 10000, 0, DUR_HOLD); sleep(3000);
        SetCondition(EFF_INERTIA, 0, 0, 0, 10000, 10000, 0, DUR_HOLD);
        // Sine rumble at two representative frequencies to confirm the period unit:
        // 50 ms = 20 Hz, then 5 ms = 200 Hz. At least one should vibrate clearly.
        SetSine(8000, 50, DUR_HOLD); sleep(3000);
        SetSine(8000, 5, DUR_HOLD); sleep(3000);
        SetSine(0, 5, DUR_HOLD);
        Console.WriteLine("[ffb] bench done");
    }

    static void sleep(int ms) { System.Threading.Thread.Sleep(ms); }

    // Release all force and shut the SDK down cleanly before exiting so the base
    // doesn't keep pulling and the SDK's internal threads are drained (avoids the
    // ZeroMQ WSASTARTUP assert on process teardown).
    public void Stop()
    {
        try { if (Ok && handle != IntPtr.Zero) { Control(MozaNative.CTRL_STOP_ALL); MozaNative.Moza_DeviceClose(handle); handle = IntPtr.Zero; } } catch { }
        if (initialized) { try { MozaNative.Moza_Shutdown(); } catch { } initialized = false; }
    }
}
