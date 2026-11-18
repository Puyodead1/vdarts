// VDarts board emulator.
// Pretends to be the dartboard controller: sends the same text lines the real
// board sends over USB serial ("A1".."L8" hits, "M1".."M5" buttons,
// "%a<ch><value>" analog sensor samples for coin / bill / miss).
//
// Transports:
//   * Named pipe \\.\pipe\VShootingKernel  (talks to the Windows VShootingKernel.dll,
//     no drivers needed - default)
//   * A COM port (use with a virtual null-modem pair such as com0com: point the DLL
//     at one end with VSHOOTING_PORT, select the other end here). In this mode the
//     lamp/config commands the game sends are shown in the log.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Pipes;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace VDartsBoardEmulator;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Scripting mode: VDartsBoardEmulator.exe --send 20 60 coin bill:10 miss up ok ...
        if (args.Length > 0 && args[0] == "--send") return Cli(args.Skip(1).ToArray());
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    static readonly Dictionary<string, int> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["up"] = 83, ["down"] = 84, ["left"] = 85, ["right"] = 86, ["ok"] = 87, ["bull"] = 81, ["dbull"] = 82,
    };

    static int Cli(string[] items)
    {
        using var pipe = new NamedPipeClientStream(".", "VShootingKernel", PipeDirection.Out);
        try { pipe.Connect(3000); }
        catch { Console.Error.WriteLine("Game/VShootingKernel.dll pipe not found"); return 2; }
        void W(params string[] lines)
        {
            var b = Encoding.ASCII.GetBytes(string.Concat(lines.Select(l => l + "\n")));
            pipe.Write(b, 0, b.Length);
            pipe.Flush();
        }
        foreach (var it in items)
        {
            if (it.StartsWith("card:", StringComparison.OrdinalIgnoreCase))
            {
                var err = VirtualCard.Send(it[5..]);
                if (err != null) { Console.Error.WriteLine(err); return 1; }
            }
            else if (it.Equals("coin", StringComparison.OrdinalIgnoreCase)) W(Protocol.Coin());
            else if (it.Equals("miss", StringComparison.OrdinalIgnoreCase)) W(Protocol.Miss());
            else if (it.StartsWith("bill:", StringComparison.OrdinalIgnoreCase))
                for (int i = 0, n = int.Parse(it[5..]); i < n; i++) { W(Protocol.BillPulse()); Thread.Sleep(Protocol.BillPulseGapMs); }
            else if (it.StartsWith("wait:", StringComparison.OrdinalIgnoreCase)) Thread.Sleep(int.Parse(it[5..]));
            else
            {
                int code = Names.TryGetValue(it, out var c) ? c : int.Parse(it);
                var line = Protocol.LineFor(code);
                if (line == null) { Console.Error.WriteLine($"no board line for code {code}"); return 1; }
                W(line);
            }
            Thread.Sleep(250); // keep well clear of the kernel's 100 ms hit debounce
        }
        return 0;
    }
}

/// Sends a card number to the VDartsMod BepInEx plugin, which hands it to the game
/// as if the cabinet's card reader had read it.
static class VirtualCard
{
    public const int Port = 47851;

    public static string Send(string number)
    {
        number = number.Trim();
        if (!VDarts.Nfc.CardNumber.IsValid(number)) return "card number must be exactly 10 digits";
        using var udp = new System.Net.Sockets.UdpClient();
        var b = Encoding.ASCII.GetBytes("CARD " + number);
        udp.Send(b, b.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, Port));
        return null;
    }
}

/// Board protocol: game code (1..87) <-> line sent by the controller.
static class Protocol
{
    // VShootingTable from libVShootingKernel.so: index = (letter-'A')*8 + (digit-'1'); M row at 96.
    static readonly int[] Table =
    {
        64, -1, 44, 33, 53,  4, 24, 13,
        67, -1, 79, 62, 63, -1, 77, 81,
        78, -1, 58, 18, 38, 26, 46,  6,
        69, -1, 49, 42, 29,  2,  9, 22,
        61, -1, 41, 50, 21, 30,  1, 10,
        74, -1, 54, 57, 34, 17, 14, 37,
        80, -1, 60, 55, 40, 35, 20, 15,
        71, -1, 51, 43, 31,  3, 11, 23,
        65, -1, 73, 75, 66,  3, 70, 82,
        68, -1, 48,  8, 28, 19, 59, 39,
        72, -1, 52, 25, 45,  5, 32, 12,
        76, -1, 56, 27, 47,  7, 36, 16,
        83, 84, 85, 86, 87, -1, -1, -1,
    };

    static readonly Dictionary<int, string> CodeToLine = Build();

    static Dictionary<int, string> Build()
    {
        var d = new Dictionary<int, string>();
        for (int i = 0; i < Table.Length; i++)
        {
            int code = Table[i];
            if (code < 0 || d.ContainsKey(code)) continue;
            d[code] = $"{(char)('A' + i / 8)}{(char)('1' + i % 8)}";
        }
        return d;
    }

    public static string LineFor(int code) => CodeToLine.TryGetValue(code, out var s) ? s : null;

    /// Game codes: 1-20 outer single, 21-40 triple, 41-60 inner single, 61-80 double, 81 bull, 82 double bull.
    public static int Code(int number, Ring ring) => ring switch
    {
        Ring.OuterSingle => number,
        Ring.Triple => 20 + number,
        Ring.InnerSingle => 40 + number,
        Ring.Double => 60 + number,
        Ring.Bull => 81,
        Ring.DoubleBull => 82,
        _ => -1,
    };

    public static string Describe(int number, Ring ring) => ring switch
    {
        Ring.OuterSingle => $"S{number} (outer)",
        Ring.InnerSingle => $"S{number} (inner)",
        Ring.Triple => $"T{number}",
        Ring.Double => $"D{number}",
        Ring.Bull => "Bull (25)",
        Ring.DoubleBull => "Double bull (50)",
        _ => "off board",
    };

    // Analog sensors: a pulse is high-low-high; the kernel fires on a falling->rising
    // turn whose swing exceeds the threshold (350 for coin/bill, 40 for the miss sensor).
    public static string[] AnalogPulse(int ch, int lo, int hi) =>
        new[] { $"%a{ch}{hi}", $"%a{ch}{lo}", $"%a{ch}{hi}" };

    public static string[] Coin() => AnalogPulse(1, 100, 900);
    public static string[] BillPulse() => AnalogPulse(2, 100, 900);
    public static string[] Miss() => AnalogPulse(0, 500, 600);

    // The kernel queues at most 5 events, so pulses must be spaced like a real acceptor.
    public const int BillPulseGapMs = 60;
}

enum Ring { None, DoubleBull, Bull, InnerSingle, Triple, OuterSingle, Double }

interface ITransport : IDisposable
{
    string Name { get; }
    bool Connected { get; }
    bool Send(string line);
    event Action<string> Received;
}

/// Keeps one persistent connection to the DLL's named pipe, reconnecting as needed.
sealed class PipeTransport : ITransport
{
    NamedPipeClientStream _pipe;
    readonly object _lock = new();
    readonly CancellationTokenSource _cts = new();
    public string Name => @"\\.\pipe\VShootingKernel";
    public bool Connected { get { lock (_lock) return _pipe?.IsConnected == true; } }
    public event Action<string> Received { add { } remove { } }

    public PipeTransport()
    {
        new Thread(ConnectLoop) { IsBackground = true, Name = "pipe-connect" }.Start();
    }

    void ConnectLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (!Connected)
            {
                var p = new NamedPipeClientStream(".", "VShootingKernel", PipeDirection.Out);
                try
                {
                    p.Connect(500);
                    lock (_lock) { _pipe?.Dispose(); _pipe = p; }
                }
                catch { p.Dispose(); }
            }
            Thread.Sleep(500);
        }
    }

    public bool Send(string line)
    {
        lock (_lock)
        {
            if (_pipe?.IsConnected != true) return false;
            try
            {
                var b = Encoding.ASCII.GetBytes(line + "\n");
                _pipe.Write(b, 0, b.Length);
                _pipe.Flush();
                return true;
            }
            catch (IOException)
            {
                _pipe.Dispose();
                _pipe = null;
                return false;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_lock) { _pipe?.Dispose(); _pipe = null; }
    }
}

/// Real serial port (other end of a virtual null-modem pair, or a USB-serial cable).
sealed class SerialTransport : ITransport
{
    readonly SerialPort _port;
    public string Name => _port.PortName;
    public bool Connected => _port.IsOpen;
    public event Action<string> Received;

    public SerialTransport(string name)
    {
        _port = new SerialPort(name, 115200, Parity.None, 8, StopBits.One)
        {
            DtrEnable = true,
            RtsEnable = true,
            WriteTimeout = 500,
        };
        _port.DataReceived += (_, _) =>
        {
            try
            {
                int n = _port.BytesToRead;
                if (n <= 0) return;
                var buf = new byte[n];
                n = _port.Read(buf, 0, n);
                // The game's commands are 4 raw bytes; show printable ones as text.
                var sb = new StringBuilder();
                for (int i = 0; i < n; i++)
                    sb.Append(buf[i] is >= 0x20 and < 0x7F ? ((char)buf[i]).ToString() : $"({buf[i]})");
                Received?.Invoke(sb.ToString());
            }
            catch { }
        };
        _port.Open();
    }

    public bool Send(string line)
    {
        if (!_port.IsOpen) return false;
        try { _port.Write(line + "\r\n"); return true; }
        catch { return false; }
    }

    public void Dispose() { try { _port.Close(); } catch { } _port.Dispose(); }
}

/// Clickable dartboard.
sealed class DartBoard : Control
{
    public static readonly int[] Order = { 20, 1, 18, 4, 13, 6, 10, 15, 2, 17, 3, 19, 7, 16, 8, 11, 14, 9, 12, 5 };
    // Regulation radii in mm (outer edge of each ring).
    const float RDBull = 6.35f, RBull = 15.9f, RTripleIn = 99f, RTripleOut = 107f, RDoubleIn = 162f, RDoubleOut = 170f, RNumbers = 200f;

    public event Action<int, Ring> Hit;
    (int num, Ring ring) _hover = (0, Ring.None);
    (int num, Ring ring, DateTime t) _flash;

    public DartBoard()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(24, 24, 28);
        Cursor = Cursors.Cross;
        var tm = new System.Windows.Forms.Timer { Interval = 50 };
        tm.Tick += (_, _) => { if (_flash.ring != Ring.None && (DateTime.Now - _flash.t).TotalMilliseconds > 400) { _flash = default; Invalidate(); } };
        tm.Start();
    }

    float Scale_ => Math.Min(Width, Height) / 2f / (RNumbers + 12f);
    PointF Center => new(Width / 2f, Height / 2f);

    public (int num, Ring ring) HitTest(Point p)
    {
        float dx = (p.X - Center.X) / Scale_, dy = (p.Y - Center.Y) / Scale_;
        double r = Math.Sqrt(dx * dx + dy * dy);
        if (r <= RDBull) return (50, Ring.DoubleBull);
        if (r <= RBull) return (25, Ring.Bull);
        if (r > RDoubleOut) return (0, Ring.None);
        double deg = Math.Atan2(dx, -dy) * 180 / Math.PI; // clockwise from 12 o'clock
        int idx = (int)Math.Floor(((deg + 9 + 360) % 360) / 18);
        int num = Order[idx];
        Ring ring = r <= RTripleIn ? Ring.InnerSingle : r <= RTripleOut ? Ring.Triple : r <= RDoubleIn ? Ring.OuterSingle : Ring.Double;
        return (num, ring);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = (0, Ring.None); Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var h = HitTest(e.Location);
        if (h.ring == Ring.None) return;
        _flash = (h.num, h.ring, DateTime.Now);
        Invalidate();
        Hit?.Invoke(h.num, h.ring);
    }

    public string HoverText => _hover.ring == Ring.None ? "" : Protocol.Describe(_hover.num, _hover.ring);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Scale_;
        var c = Center;

        Color black = Color.FromArgb(30, 30, 30), cream = Color.FromArgb(235, 222, 190),
              red = Color.FromArgb(200, 40, 40), green = Color.FromArgb(30, 140, 70);

        using (var b = new SolidBrush(Color.FromArgb(12, 12, 12)))
            g.FillEllipse(b, c.X - RNumbers * s, c.Y - RNumbers * s, 2 * RNumbers * s, 2 * RNumbers * s);

        for (int i = 0; i < 20; i++)
        {
            int num = Order[i];
            float start = -90 - 9 + i * 18; // GDI angles: 0 = 3 o'clock, clockwise
            bool dark = i % 2 == 0;
            Wedge(g, c, RDoubleOut * s, RDoubleIn * s, start, Pick(num, Ring.Double, dark ? red : green));
            Wedge(g, c, RDoubleIn * s, RTripleOut * s, start, Pick(num, Ring.OuterSingle, dark ? black : cream));
            Wedge(g, c, RTripleOut * s, RTripleIn * s, start, Pick(num, Ring.Triple, dark ? red : green));
            Wedge(g, c, RTripleIn * s, RBull * s, start, Pick(num, Ring.InnerSingle, dark ? black : cream));

            double a = (i * 18 - 90) * Math.PI / 180;
            float rr = (RDoubleOut + RNumbers) / 2 * s;
            using var f = new Font("Segoe UI", Math.Max(8, 15 * s), FontStyle.Bold);
            var str = num.ToString();
            var sz = g.MeasureString(str, f);
            g.DrawString(str, f, Brushes.WhiteSmoke, c.X + (float)Math.Cos(a) * rr - sz.Width / 2, c.Y + (float)Math.Sin(a) * rr - sz.Height / 2);
        }
        using (var b = new SolidBrush(Pick(25, Ring.Bull, green)))
            g.FillEllipse(b, c.X - RBull * s, c.Y - RBull * s, 2 * RBull * s, 2 * RBull * s);
        using (var b = new SolidBrush(Pick(50, Ring.DoubleBull, red)))
            g.FillEllipse(b, c.X - RDBull * s, c.Y - RDBull * s, 2 * RDBull * s, 2 * RDBull * s);
        using (var pen = new Pen(Color.FromArgb(160, 160, 160), 1))
            foreach (var rad in new[] { RDoubleOut, RDoubleIn, RTripleOut, RTripleIn, RBull })
                g.DrawEllipse(pen, c.X - rad * s, c.Y - rad * s, 2 * rad * s, 2 * rad * s);
    }

    Color Pick(int num, Ring ring, Color baseColor)
    {
        if (_flash.ring == ring && _flash.num == num) return Color.Gold;
        if (_hover.ring == ring && _hover.num == num)
            return Color.FromArgb(Math.Min(255, baseColor.R + 60), Math.Min(255, baseColor.G + 60), Math.Min(255, baseColor.B + 60));
        return baseColor;
    }

    static void Wedge(Graphics g, PointF c, float rOut, float rIn, float start, Color color)
    {
        using var path = new GraphicsPath();
        path.AddArc(c.X - rOut, c.Y - rOut, 2 * rOut, 2 * rOut, start, 18);
        path.AddArc(c.X - rIn, c.Y - rIn, 2 * rIn, 2 * rIn, start + 18, -18);
        path.CloseFigure();
        using var b = new SolidBrush(color);
        g.FillPath(b, path);
    }
}

sealed class MainForm : Form
{
    readonly DartBoard _board = new() { Dock = DockStyle.Fill };
    readonly ComboBox _transportBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    readonly Label _status = new() { AutoSize = true, Padding = new Padding(6, 6, 0, 0) };
    readonly Label _hover = new() { AutoSize = true, Padding = new Padding(6, 6, 0, 0), ForeColor = Color.DimGray };
    readonly ListBox _log = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 9), IntegralHeight = false };
    readonly NumericUpDown _billCount = new() { Minimum = 1, Maximum = 500, Value = 10, Width = 60 };
    readonly Label _nfcStatus = new() { AutoSize = false, Width = 290, Height = 18, ForeColor = Color.DimGray, Text = "NFC: starting…" };
    readonly TextBox _cardNumber = new() { Width = 120, MaxLength = 10, Font = new Font("Consolas", 10) };
    readonly CheckBox _forward = new() { Text = "Send reader taps to game (off if VDartsMod reads the reader itself)", AutoSize = false, Width = 290, Height = 34 };
    readonly Label _cardInfo = new() { AutoSize = false, Width = 290, Height = 48, Font = new Font("Consolas", 8.5f) };
    const string StaffCard = "2513081404"; // built-in VDarts staff GameKey card
    VDarts.Nfc.PcscReader _reader;
    string _lastNumber;
    ITransport _transport;

    public MainForm()
    {
        Text = "VDarts Board Emulator";
        ClientSize = new Size(1000, 900);
        MinimumSize = new Size(760, 560);
        KeyPreview = true;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(4) };
        top.Controls.Add(new Label { Text = "Connect via:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        top.Controls.Add(_transportBox);
        var refresh = new Button { Text = "Refresh ports", AutoSize = true };
        refresh.Click += (_, _) => FillTransports();
        top.Controls.Add(refresh);
        top.Controls.Add(_status);
        top.Controls.Add(_hover);

        var side = new TableLayoutPanel { Dock = DockStyle.Right, Width = 320, ColumnCount = 1, Padding = new Padding(6) };
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var keys = new GroupBox { Text = "Cabinet buttons", Dock = DockStyle.Top, Height = 150 };
        var kp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
        for (int i = 0; i < 3; i++) { kp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f)); kp.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3f)); }
        kp.Controls.Add(Btn("▲ Up", 83), 1, 0);
        kp.Controls.Add(Btn("◀ Left", 85), 0, 1);
        kp.Controls.Add(Btn("OK", 87), 1, 1);
        kp.Controls.Add(Btn("Right ▶", 86), 2, 1);
        kp.Controls.Add(Btn("▼ Down", 84), 1, 2);
        keys.Controls.Add(kp);

        var money = new GroupBox { Text = "Money / sensors", Dock = DockStyle.Top, Height = 150 };
        var mp = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var coin = new Button { Text = "Insert coin", Width = 120, Height = 30 };
        coin.Click += (_, _) => SendCoin();
        var bill = new Button { Text = "Bill pulses:", Width = 120, Height = 30 };
        bill.Click += (_, _) => SendBill((int)_billCount.Value);
        var miss = new Button { Text = "Miss (dart off board)", Width = 245, Height = 30 };
        miss.Click += (_, _) => SendMiss();
        mp.Controls.AddRange(new Control[] { coin, new Label { Width = 140 }, bill, _billCount, miss });
        money.Controls.Add(mp);

        var help = new Label
        {
            AutoSize = false, Dock = DockStyle.Top, Height = 92, ForeColor = Color.DimGray,
            Text = "Click the board to throw a dart.\nKeys: arrows/WASD = buttons, Enter = OK,\nC = coin, B = bill, M = miss.\nThe game ignores darts < 0.6 s apart.",
        };

        var cards = new GroupBox { Text = "Cards (member / GameKey)", Dock = DockStyle.Top, Height = 215 };
        var cp = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var tap = new Button { Text = "Tap virtual card", Width = 125, Height = 28 };
        tap.Click += (_, _) => TapVirtual(_cardNumber.Text);
        var write = new Button { Text = "Write to NFC card", Width = 125, Height = 28 };
        write.Click += (_, _) => WriteCard();
        var oper = new Button { Text = "Operator card", Width = 125, Height = 28 };
        oper.Click += (_, _) => TapVirtual(StaffCard);
        var useLast = new Button { Text = "Copy from reader", Width = 125, Height = 28 };
        useLast.Click += (_, _) => { if (_lastNumber != null) _cardNumber.Text = _lastNumber; };
        cp.Controls.AddRange(new Control[]
        {
            _nfcStatus, new Label { Text = "Number:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _cardNumber,
            tap, write, useLast, oper, _forward, _cardInfo,
        });
        cards.Controls.Add(cp);

        var logBox = new GroupBox { Text = "Log", Dock = DockStyle.Fill };
        logBox.Controls.Add(_log);

        side.RowStyles.Insert(0, new RowStyle(SizeType.AutoSize));
        side.Controls.Add(keys, 0, 0);
        side.Controls.Add(money, 0, 1);
        side.Controls.Add(cards, 0, 2);
        side.Controls.Add(help, 0, 3);
        side.Controls.Add(logBox, 0, 4);

        Controls.Add(_board);
        Controls.Add(side);
        Controls.Add(top);

        _board.Hit += (num, ring) => SendCode(Protocol.Code(num, ring), Protocol.Describe(num, ring));
        _board.MouseMove += (_, _) => _hover.Text = _board.HoverText;

        _transportBox.SelectedIndexChanged += (_, _) => SwitchTransport();
        FillTransports();

        var tm = new System.Windows.Forms.Timer { Interval = 300 };
        tm.Tick += (_, _) => UpdateStatus();
        tm.Start();

        _reader = new VDarts.Nfc.PcscReader("");
        _reader.StatusChanged += s => BeginInvoke(() => _nfcStatus.Text = "NFC: " + s);
        _reader.CardPresented += c => BeginInvoke(() => OnCard(c));
        _reader.CardRemoved += () => BeginInvoke(() => _cardInfo.ForeColor = Color.DimGray);
    }

    void OnCard(VDarts.Nfc.CardInfo c)
    {
        var be = VDarts.Nfc.CardNumber.FromUid(c.Uid, VDarts.Nfc.UidOrder.BigEndian);
        var le = VDarts.Nfc.CardNumber.FromUid(c.Uid, VDarts.Nfc.UidOrder.LittleEndian);
        _lastNumber = c.Stored ?? be;
        _cardInfo.ForeColor = SystemColors.ControlText;
        _cardInfo.Text = $"UID {c.UidHex}  {c.Kind}\nwritten: {c.Stored ?? (c.Error ?? "none")}\nfrom UID: BE {be}  LE {le}";
        Log($"NFC card {c.UidHex} ({c.Kind}) written={c.Stored ?? "none"} BE={be} LE={le}");
        if (_forward.Checked) TapVirtual(_lastNumber);
    }

    void TapVirtual(string number)
    {
        var err = VirtualCard.Send(number);
        Log(err == null ? $"-> card {number.Trim()} (to VDartsMod, udp {VirtualCard.Port})" : $"!! card: {err}");
    }

    void WriteCard()
    {
        var number = _cardNumber.Text.Trim();
        if (!VDarts.Nfc.CardNumber.IsValid(number)) { Log("!! write: card number must be exactly 10 digits"); return; }
        Log($"Writing {number}: hold a card on the reader (10 s)…");
        var r = _reader;
        new Thread(() =>
        {
            var err = r.Write(number, 10000);
            BeginInvoke(() => Log(err == null ? $"Wrote {number} to card (verified)" : $"!! write failed: {err}"));
        }) { IsBackground = true }.Start();
    }

    Button Btn(string text, int code)
    {
        var b = new Button { Text = text, Dock = DockStyle.Fill, TabStop = false };
        b.Click += (_, _) => SendCode(code, text.Trim('▲', '▼', '◀', '▶', ' '));
        return b;
    }

    const string PipeItem = "Named pipe (VShootingKernel.dll)";

    void FillTransports()
    {
        var cur = _transportBox.SelectedItem as string;
        _transportBox.Items.Clear();
        _transportBox.Items.Add(PipeItem);
        foreach (var p in SerialPort.GetPortNames().Distinct().OrderBy(p => p.Length).ThenBy(p => p))
            _transportBox.Items.Add(p);
        _transportBox.SelectedItem = cur != null && _transportBox.Items.Contains(cur) ? cur : PipeItem;
    }

    void SwitchTransport()
    {
        _transport?.Dispose();
        _transport = null;
        var sel = _transportBox.SelectedItem as string;
        try
        {
            _transport = sel == PipeItem || sel == null ? new PipeTransport() : new SerialTransport(sel);
            _transport.Received += s => BeginInvoke(() => Log($"<- {s}   (lamp/config command from game)"));
            Log($"Using {_transport.Name}");
        }
        catch (Exception ex)
        {
            Log($"Cannot open {sel}: {ex.Message}");
        }
        UpdateStatus();
    }

    void UpdateStatus()
    {
        bool ok = _transport?.Connected == true;
        _status.Text = ok ? "● connected" : (_transport is PipeTransport ? "○ waiting for game…" : "○ not open");
        _status.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
    }

    void Send(string what, params string[] lines)
    {
        if (_transport == null || !_transport.Connected)
        {
            Log($"!! not connected - {what} dropped");
            return;
        }
        foreach (var l in lines)
            if (!_transport.Send(l)) { Log($"!! send failed - {what}"); return; }
        Log($"-> {string.Join(" ", lines),-18} {what}");
    }

    void SendCode(int code, string what)
    {
        var line = Protocol.LineFor(code);
        if (line != null) Send($"{what} [{code}]", line);
    }

    void SendCoin() => Send("coin [100]", Protocol.Coin());

    void SendBill(int pulses)
    {
        if (_transport == null || !_transport.Connected) { Log($"!! not connected - bill dropped"); return; }
        Log($"-> bill x{pulses} pulses [101+n]");
        var t = _transport;
        new Thread(() =>
        {
            for (int i = 0; i < pulses; i++)
            {
                foreach (var l in Protocol.BillPulse()) if (!t.Send(l)) return;
                Thread.Sleep(Protocol.BillPulseGapMs);
            }
        }) { IsBackground = true }.Start();
    }

    void SendMiss() => Send("miss [0]", Protocol.Miss());

    void Log(string s)
    {
        _log.Items.Add($"{DateTime.Now:HH:mm:ss.fff} {s}");
        if (_log.Items.Count > 500) _log.Items.RemoveAt(0);
        _log.TopIndex = _log.Items.Count - 1;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_transportBox.Focused || _billCount.Focused || _cardNumber.Focused) return base.ProcessCmdKey(ref msg, keyData);
        switch (keyData)
        {
            case Keys.Up: case Keys.W: SendCode(83, "Up"); return true;
            case Keys.Down: case Keys.S: SendCode(84, "Down"); return true;
            case Keys.Left: case Keys.A: SendCode(85, "Left"); return true;
            case Keys.Right: case Keys.D: SendCode(86, "Right"); return true;
            case Keys.Enter: SendCode(87, "OK"); return true;
            case Keys.C: SendCoin(); return true;
            case Keys.B: SendBill((int)_billCount.Value); return true;
            case Keys.M: SendMiss(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _transport?.Dispose();
        _reader?.Dispose();
        base.OnFormClosed(e);
    }
}
