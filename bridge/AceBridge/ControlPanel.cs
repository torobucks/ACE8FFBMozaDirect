// ControlPanel.cs — small live-tuning GUI for AceBridge.
//
// The window shows two sliders that adjust the master force strength (Gain) and
// the rumble/vibration scale (Vib) in real time, plus a log panel that mirrors
// everything the program prints (our own [ffb]/[pipe]/[live] lines AND the MOZA
// SDK's internal INFO/DEBUG/WARN logs via its official log callback).
//
// AceBridge is built as a GUI subsystem (WinExe): no console window exists. All
// output goes to the log panel. If stdout/stderr are redirected (e.g. a script
// captures `AceBridge --test`), .NET writes to the redirected pipe instead, so
// scripted probes still see the text.
//
// Closing this window exits AceBridge cleanly (forces released, SDK shut down).
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

// Thread-safe log funnel: every Console.WriteLine is routed here by a TextWriter
// (a no-op when no console/redirect exists, i.e. normal GUI use), and the SDK log
// callback pushes its lines here too. A WinForms timer drains the queue into the
// log TextBox in batches, so per-frame SDK DEBUG lines cannot flood the UI thread.
static class LogHub
{
    public static bool Gui;                       // true when the control panel is running
    public static TextBox LogBox;                // set by the control panel
    public static TextWriter RealOut;            // original console writer (Null when no console)
    static readonly ConcurrentQueue<string> Q = new();
    static readonly StringBuilder Sb = new();
    const int MaxQueue = 4000, MaxChars = 120000, DrainPerTick = 300;

    sealed class DuplexWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char c) { RealOut.Write(c); Push(c.ToString()); }
        public override void Write(string s) { RealOut.Write(s); Push(s); }
    }

    // Capture the original console writer and install the duplicating writer. Call
    // once before any program output that should be mirrored.
    public static void Install()
    {
        if (!Gui) return;
        RealOut = Console.Out;   // TextWriter.Null when no console is attached
        var w = new DuplexWriter();
        Console.SetOut(w);
        Console.SetError(w);
    }

    public static void Push(string s)
    {
        if (!Gui || string.IsNullOrEmpty(s)) return;
        Q.Enqueue(s.Replace("\r", ""));
        while (Q.Count > MaxQueue) Q.TryDequeue(out _);
    }

    // Called on the UI thread by a timer: move queued lines into the TextBox.
    public static void Drain()
    {
        if (!Gui || LogBox == null) return;
        var chunk = new StringBuilder();
        for (int i = 0; i < DrainPerTick && Q.TryDequeue(out var s); i++) chunk.Append(s);
        if (chunk.Length == 0) return;
        Sb.Append(chunk);
        if (Sb.Length > MaxChars) Sb.Remove(0, Sb.Length - MaxChars / 2);
        LogBox.Text = Sb.ToString();
        LogBox.SelectionStart = LogBox.TextLength;
        LogBox.ScrollToCaret();
    }
}

public sealed class ControlPanel : Form
{
    readonly MozaFfb ffb;

    internal ControlPanel(MozaFfb ffb)
    {
        this.ffb = ffb;
        Text = "AceFFB Control — AC8 -> MOZA SDK direct FFB";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(540, 460);

        // ---- Gain row ----
        var gainLabel = new Label { Text = "Gain", Location = new Point(12, 16), AutoSize = true };
        var gainBar = new TrackBar
        {
            Minimum = 0, Maximum = 200, TickFrequency = 20, SmallChange = 5, LargeChange = 20,
            Value = (int)Math.Round(Math.Clamp(ffb.Gain, 0, 2) * 100),
            Location = new Point(60, 8), Width = 320
        };
        var gainVal = new Label
        {
            Text = $"{ffb.Gain:0.00}x", Location = new Point(392, 16), AutoSize = true,
            Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold)
        };
        gainBar.ValueChanged += (_, _) =>
        {
            ffb.Gain = gainBar.Value / 100.0;
            gainVal.Text = $"{ffb.Gain:0.00}x";
        };

        // ---- Vib row ----
        var vibLabel = new Label { Text = "Vib", Location = new Point(12, 52), AutoSize = true };
        var vibBar = new TrackBar
        {
            Minimum = 0, Maximum = 200, TickFrequency = 20, SmallChange = 5, LargeChange = 20,
            Value = (int)Math.Round(Math.Clamp(ffb.Scale, 0, 2) * 100),
            Location = new Point(60, 44), Width = 320
        };
        var vibVal = new Label
        {
            Text = $"{ffb.Scale:0.00}x", Location = new Point(392, 52), AutoSize = true,
            Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold)
        };
        vibBar.ValueChanged += (_, _) =>
        {
            ffb.Scale = vibBar.Value / 100.0;
            vibVal.Text = $"{ffb.Scale:0.00}x";
        };

        // ---- Log panel ----
        LogHub.LogBox = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
            Location = new Point(12, 88), Size = new Size(516, 330),
            Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(10, 12, 16), ForeColor = Color.LightGray
        };

        var status = new Label { Text = "Profile: --", Location = new Point(12, 426), AutoSize = true };
        var hint = new Label
        {
            Text = "Close this window to exit AceBridge cleanly.",
            Location = new Point(230, 426), AutoSize = true, ForeColor = Color.Gray
        };

        Controls.AddRange(new Control[]
        {
            gainLabel, gainBar, gainVal,
            vibLabel, vibBar, vibVal,
            LogHub.LogBox, status, hint
        });

        // Drains log lines and refreshes the profile readout every 250 ms.
        var timer = new Timer { Interval = 250 };
        timer.Tick += (_, _) =>
        {
            LogHub.Drain();
            status.Text = "Profile: " + (string.IsNullOrEmpty(ffb.CurrentProfile) ? "--" : ffb.CurrentProfile);
        };
        timer.Start();

        // Clean shutdown: release forces and drain the SDK before the process exits.
        FormClosed += (_, _) => { ffb.Stop(); Application.Exit(); };
    }
}
