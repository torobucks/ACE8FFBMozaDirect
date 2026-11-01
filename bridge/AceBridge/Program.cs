// AceBridge: reads AC8 telemetry from a named pipe (written by the AceTelemetry UE4SS
// mod) and drives the MOZA flight base DIRECTLY through the official MOZA SDK
// (MOZA_SDK.dll). The old DCS / MOZA-Cockpit / telemetry-faking pipeline is gone:
// the centering spring (scaled by G-load and airspeed), damper, friction, inertia
// and every rumble effect are sent straight to the base via MainCtrl_Ffb* commands.
using System; using System.Globalization; using System.IO; using System.IO.Pipes; using System.Text;
using System.Threading; using System.Diagnostics; using System.Collections.Generic; using System.Text.Json;
using System.Windows.Forms;

class Sample { public string plane = ""; public double gun, ice, cdens; public double t, spd, vx, vy, vz, pitch, yaw, roll, health, maxHealth, missile, throttle, cloud; public bool valid; }

class FfbProfile {
  public string Name;
  public double Spring = 30, Damper = 30, Friction = 0, Inertia = 0, Rumble = 100;
  public double Gain = 1.0, GLoad = 1.0, SpeedLoad = 1.0;
  public static FfbProfile Parse(string name, JsonElement e) {
    var p = new FfbProfile { Name = name };
    if (e.TryGetProperty("spring", out var v)) p.Spring = GetNum(v, p.Spring);
    if (e.TryGetProperty("damper", out v)) p.Damper = GetNum(v, p.Damper);
    if (e.TryGetProperty("friction", out v)) p.Friction = GetNum(v, p.Friction);
    if (e.TryGetProperty("inertia", out v)) p.Inertia = GetNum(v, p.Inertia);
    if (e.TryGetProperty("rumble", out v)) p.Rumble = GetNum(v, p.Rumble);
    if (e.TryGetProperty("gain", out v)) p.Gain = GetNum(v, p.Gain);
    if (e.TryGetProperty("gload", out v)) p.GLoad = GetNum(v, p.GLoad);
    if (e.TryGetProperty("speedload", out v)) p.SpeedLoad = GetNum(v, p.SpeedLoad);
    return p;
  }
  static double GetNum(JsonElement e, double dflt) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : dflt;
}

static class Program {
  static readonly object Gate = new();
  static Sample Latest = new();
  static long Frames;
  static string Aircraft = "FA-18C-AC8";
  static double VibScale = 1.0, GainScale = 1.0, StallKmh = 200;
  static string MapPath = null; static bool TestMode = false, NoGui = false;
  static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

  [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int h);
  [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint m);
  [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint m);
  static void DisableQuickEdit() { try { var h = GetStdHandle(-10); if (GetConsoleMode(h, out var m)) SetConsoleMode(h, (m & ~0x0040u) | 0x0080u); } catch { } }

  [STAThread] static void Main(string[] args) {
    DisableQuickEdit();
    for (int i = 0; i < args.Length; i++) {
      string a = args[i];
      if (a.StartsWith("--")) a = a.Substring(2);
      else if (a.StartsWith("-") && a.Length > 1) a = a.Substring(1);
      else if (a == "/?") a = "help";
      if (a == "help" || a == "h") {
        Console.WriteLine("AceBridge [--aircraft FA-18C-AC8] [--vib 1.0] [--gain 1.0] [--stall-kmh 200] [--map path\\ffb-profiles.json] [--nogui] [--test]");
        Console.WriteLine("  --aircraft  fallback force profile when the plane is unknown (default FA-18C-AC8)");
        Console.WriteLine("  --vib       rumble scale (default 1.0)");
        Console.WriteLine("  --gain      master strength for all forces (default 1.0)");
        Console.WriteLine("  --stall-kmh stall-shake onset airspeed, km/h (default 200)");
        Console.WriteLine("  --map       path to ffb-profiles.json (default: auto-detect)");
        Console.WriteLine("  --nogui     no control panel; console output only (default: GUI with Gain/Vib sliders + live log)");
        Console.WriteLine("  --test      run the force-feedback bench, then exit (calibrate on your base)");
        Console.WriteLine("  (single-dash forms like -test also work)");
        return;
      }
      if (a == "aircraft") { if (i + 1 < args.Length) Aircraft = args[i + 1]; }
      else if (a == "vib") { if (i + 1 < args.Length) VibScale = double.Parse(args[i + 1], Inv); }
      else if (a == "gain") { if (i + 1 < args.Length) GainScale = double.Parse(args[i + 1], Inv); }
      else if (a == "stall-kmh") { if (i + 1 < args.Length) StallKmh = double.Parse(args[i + 1], Inv); }
      else if (a == "map") { if (i + 1 < args.Length) MapPath = args[i + 1]; }
      else if (a == "test") TestMode = true;
      else if (a == "nogui") NoGui = true;
    }
    // In GUI mode every console line (ours + the SDK's own logs) is also mirrored
    // into the control panel's log box. --test stays console-only for diagnostics.
    bool gui = !NoGui && !TestMode;
    LogHub.Gui = gui;
    LogHub.Install();
    if (gui) MozaNative.LogSink = LogHub.Push;
    Console.WriteLine($"AceBridge: pipe \\\\.\\pipe\\AceTelemetry -> MOZA SDK direct FFB, aircraft profile {Aircraft}, vib {VibScale:0.00}, gain {GainScale:0.00}");
    LoadProfiles();
    var ffb = new MozaFfb(VibScale, GainScale);
    // Clean shutdown on Ctrl+C / window close so the SDK's threads are drained
    // (otherwise the ZeroMQ WSASTARTUP assert fires at teardown).
    Console.CancelKeyPress += (s, e) => { e.Cancel = true; Console.WriteLine("[ffb] interrupted: releasing force and shutting down the SDK cleanly..."); ffb.Stop(); Environment.Exit(0); };
    if (TestMode) { ffb.Test(); ffb.Stop(); Console.WriteLine("done"); return; }
    new Thread(PipeLoop) { IsBackground = true }.Start();
    if (gui) {
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      new Thread(() => ForceLoop(ffb)) { IsBackground = true, Name = "ForceLoop" }.Start();
      Application.Run(new ControlPanel(ffb));
      return;
    }
    ForceLoop(ffb);
  }

  // ---- profiles ----
  static Dictionary<string, FfbProfile> Profiles = new();
  static List<(string pre, string prof)> Map = new();
  static string MapDefault = "FA-18C-AC8";
  static FfbProfile Fallback;
  static void LoadProfiles() {
    try {
      string b = AppContext.BaseDirectory;
      var candidates = new List<string>();
      if (MapPath != null) candidates.Add(MapPath);
      candidates.Add(Path.Combine(b, "ffb-profiles.json"));
      candidates.Add(Path.Combine(b, "..", "config", "ffb-profiles.json"));
      candidates.Add(Path.Combine(b, "..", "..", "..", "..", "..", "config", "ffb-profiles.json"));
      candidates.Add(Path.Combine(Environment.CurrentDirectory, "config", "ffb-profiles.json"));
      string path = candidates.Find(File.Exists) ?? candidates[0];
      using var doc = JsonDocument.Parse(File.ReadAllText(path));
      var root = doc.RootElement;
      MapDefault = root.GetProperty("default").GetString();
      foreach (var e in root.GetProperty("map").EnumerateArray()) Map.Add((e[0].GetString(), e[1].GetString()));
      foreach (var p in root.GetProperty("profiles").EnumerateObject()) Profiles[p.Name] = FfbProfile.Parse(p.Name, p.Value);
      Fallback = Profiles.TryGetValue(MapDefault, out var d) ? d : new FfbProfile { Name = MapDefault };
      Console.WriteLine($"[ffb] {Profiles.Count} force profiles, {Map.Count} mappings loaded from {Path.GetFullPath(path)}");
    } catch (Exception ex) { Console.WriteLine("[ffb] using built-in default profile: " + ex.Message); Fallback = new FfbProfile { Name = MapDefault }; }
  }
  static string Normalize(string code) => System.Text.RegularExpressions.Regex.Replace(code.ToLowerInvariant(), @"(?<![0-9])0+(?=[0-9])", "");
  static string LastPlane = null, LastProfile = null;
  static string ProfileFor(string plane) {
    if (string.IsNullOrEmpty(plane)) return Aircraft;
    if (plane == LastPlane) return LastProfile;
    var m = System.Text.RegularExpressions.Regex.Match(plane, @"PP\d+_([A-Za-z0-9]+)");
    var code = Normalize(m.Success ? m.Groups[1].Value : plane);
    string prof = null; foreach (var (pre, pr) in Map) if (code.StartsWith(pre)) { prof = pr; break; }
    if (prof == null) { prof = MapDefault; Console.WriteLine($"[ffb] no profile for '{code}' ({plane}); using {prof}. Add it to ffb-profiles.json"); }
    else if (plane != LastPlane) Console.WriteLine($"[ffb] plane {code} -> profile {prof}");
    LastPlane = plane; LastProfile = prof; return prof;
  }
  static FfbProfile ProfileOf(string name) => Profiles.TryGetValue(name, out var p) ? p : (Fallback ?? new FfbProfile { Name = name });

  // ---- pipe ----
  static void PipeLoop() {
    var clock = Stopwatch.StartNew();
    while (true) {
      try {
        using var pipe = new NamedPipeServerStream("AceTelemetry", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
        Console.WriteLine("[pipe] waiting for game mod...");
        pipe.WaitForConnection();
        Console.WriteLine("[pipe] game mod connected");
        using var rd = new StreamReader(pipe, Encoding.ASCII);
        string line;
        while ((line = rd.ReadLine()) != null) {
          var s = Parse(line); s.t = clock.Elapsed.TotalSeconds;
          lock (Gate) { Latest = s; Frames++; }
        }
        Console.WriteLine("[pipe] game mod disconnected");
        lock (Gate) { Latest = new Sample(); }
      } catch (Exception ex) { Console.WriteLine("[pipe] " + ex.Message); Thread.Sleep(1000); }
    }
  }
  static Sample Parse(string line) {
    var s = new Sample();
    if (line.StartsWith("none")) return s;
    foreach (var kv in line.Split(';')) {
      var p = kv.Split('='); if (p.Length != 2) continue;
      if (p[0] == "pl") { s.plane = p[1]; continue; }
      if (!double.TryParse(p[1], NumberStyles.Float, Inv, out var v)) continue;
      switch (p[0]) {
        case "spd": s.spd = v; break; case "vx": s.vx = v; break; case "vy": s.vy = v; break; case "vz": s.vz = v; break;
        case "p": s.pitch = v; break; case "y": s.yaw = v; break; case "r": s.roll = v; break;
        case "hp": s.health = v; break; case "mhp": s.maxHealth = v; break; case "ma": s.missile = v; break;
        case "thr": s.throttle = v; break; case "cl": s.cloud = v; break; case "gf": s.gun = v; break; case "ic": s.ice = v; break; case "cd": s.cdens = v; break;
      }
    }
    s.valid = true; return s;
  }

  // ---- kinematics (unchanged from upstream) ----
  static double D2R(double d) => d * Math.PI / 180.0;
  static (double x, double y, double z) Fwd(double p, double y) { p = D2R(p); y = D2R(y); return (Math.Cos(p) * Math.Cos(y), Math.Cos(p) * Math.Sin(y), Math.Sin(p)); }
  static (double x, double y, double z) Up(double p, double y, double r) {
    p = D2R(p); y = D2R(y); r = D2R(r);
    double cp = Math.Cos(p), sp = Math.Sin(p), cy = Math.Cos(y), sy = Math.Sin(y), cr = Math.Cos(r), sr = Math.Sin(r);
    return (-(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp); // UE FRotationMatrix Z axis
  }
  static (double x, double y, double z) Right(double p, double y, double r) {
    p = D2R(p); y = D2R(y); r = D2R(r);
    double cp = Math.Cos(p), sp = Math.Sin(p), cy = Math.Cos(y), sy = Math.Sin(y), cr = Math.Cos(r), sr = Math.Sin(r);
    return (sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp); // UE FRotationMatrix Y axis
  }
  static double Dot((double x, double y, double z) a, double bx, double by, double bz) => a.x * bx + a.y * by + a.z * bz;
  static double WrapDeg(double d) { while (d > 180) d -= 360; while (d < -180) d += 360; return d; }

  // ---- force loop: telemetry in, force out (direct to the base) ----
  static void ForceLoop(MozaFfb ffb) {
    Sample prev = null; double gSmooth = 1, lastHp = -1, hpFull = 0;
    double pv = 0, yv = 0, rv = 0;
    bool iced = false, gunOn = false, boomArmed = false, airborne = false, stalling = false;
    var statusClock = Stopwatch.StartNew(); bool idle = true;
    var idleTelemetryClock = Stopwatch.StartNew();
    while (true) {
      Sample s; long frames; lock (Gate) { s = Latest; frames = Frames; }
      if (!s.valid) {
        if (!idle) { idle = true; ffb.Idle(); Console.WriteLine("[ffb] no aircraft (menu/loading) - centering spring only"); }
        if (idleTelemetryClock.ElapsedMilliseconds > 8000) {
          idleTelemetryClock.Restart();
          Console.WriteLine("[ffb] still no game telemetry. If this keeps up while flying, the AC8 mod is not loaded (UE4SS / AceTelemetry / EAC-off launch). Only a gentle centering spring is active.");
        }
        prev = null; lastHp = -1; hpFull = 0; Thread.Sleep(50); continue;
      }
      idle = false;
      if (prev != null && s.t > prev.t + 1e-4) {
        double dt = s.t - prev.t;
        double ax = (s.vx - prev.vx) / dt, ay = (s.vy - prev.vy) / dt, az = (s.vz - prev.vz) / dt + 981.0;
        var up = Up(s.pitch, s.yaw, s.roll); var fw = Fwd(s.pitch, s.yaw); var rt = Right(s.pitch, s.yaw, s.roll);
        double nz = Dot(up, ax, ay, az) / 981.0;
        nz = Math.Clamp(nz, -4, 12);
        double a = Math.Min(1, dt / 0.12);
        gSmooth += (nz - gSmooth) * a;
        double k = Math.Min(1, dt / 0.08);
        pv += (D2R(WrapDeg(s.pitch - prev.pitch)) / dt - pv) * k; yv += (D2R(WrapDeg(s.yaw - prev.yaw)) / dt - yv) * k; rv += (D2R(WrapDeg(s.roll - prev.roll)) / dt - rv) * k;
      }
      if (s.health > hpFull) hpFull = s.health;
      if (lastHp >= 0 && s.health < lastHp - 0.5 && hpFull > 0) {
        double frac = (lastHp - s.health) / hpFull;
        string tier;
        if (s.health <= 0) { ffb.Pulse(0.85, 80, 900); tier = "DESTROYED"; }
        else if (frac >= 0.10) { ffb.Pulse(0.65, 60, 420); tier = "heavy (missile/bump)"; }
        else { ffb.Pulse(0.35, 35, 160); tier = "light (bullets)"; }
        Console.WriteLine($"[hit] {tier}: health {lastHp} -> {s.health} ({frac:P0} of {hpFull})");
      }
      lastHp = s.health;
      if (prev == null || s.t != prev.t) prev = s;

      double tas = s.spd / 100.0; // cm/s -> m/s
      if (s.gun > 0) { if (!gunOn) Console.WriteLine("[gun] firing"); gunOn = true; } else { if (gunOn) Console.WriteLine("[gun] stopped"); gunOn = false; }
      if (s.ice > 0) { if (!iced) Console.WriteLine("[ice] icing started"); iced = true; } else if (iced) { iced = false; Console.WriteLine("[ice] icing cleared"); }
      double machNow = tas / 340.0;
      if (machNow >= 1.0 && boomArmed) { boomArmed = false; ffb.Pulse(0.42, 100, 280); Console.WriteLine($"[boom] sonic boom at {tas * 3.6:0} km/h"); }
      if (machNow < 0.93) boomArmed = true;
      double kmh = tas * 3.6;
      if (kmh > StallKmh + 120) airborne = true; else if (kmh < 60) airborne = false;
      double stall = airborne ? Math.Clamp((StallKmh - kmh) / 90.0 + 0.35, 0, 1) : 0;
      if (kmh >= StallKmh) stall = 0;
      if ((stall > 0) != stalling) { stalling = stall > 0; Console.WriteLine(stalling ? $"[stall] stall shake on at {kmh:0} km/h" : $"[stall] off at {kmh:0} km/h"); }

      string profile = ProfileFor(s.plane);
      var pr = ProfileOf(profile);
      double cloud = (s.cloud > 0 || s.cdens > 0.01) ? Math.Clamp(s.cdens, 0, 1) : 0;
      ffb.Update(pr, gSmooth, tas, s.gun > 0 ? 1 : 0, cloud, s.ice > 0 ? 1 : 0, stall);

      if (statusClock.ElapsedMilliseconds > 2000) {
        statusClock.Restart();
        Console.WriteLine($"[live] frames={frames} tas={tas:0} m/s G={gSmooth:0.0} hp={s.health} thr={s.throttle:0.00} gun={s.gun} cloud={s.cdens:0.00} ice={s.ice} profile={profile}");
      }
      Thread.Sleep(16);
    }
  }
}
