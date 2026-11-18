// PC/SC NFC card reader/writer shared by the BepInEx plugin (net472, runs inside
// the game's Mono) and the board emulator (net8). Keep it C# 7.3 compatible.
//
// VDarts identifies member cards and GameKey (operator) cards by a 10 digit
// decimal number. The original cabinet reader typed that number like a keyboard.
// Here a card's number is, in order of preference:
//   1. a number written to the card by the emulator ("VDC1" record, see below)
//   2. an entry in the UID -> number map file
//   3. the first 4 UID bytes as an unsigned 32-bit decimal, zero padded to 10
//      digits (what common 10-digit keyboard-wedge readers print)
//
// Stored record (16 bytes): "VDC1" + 10 ASCII digits + checksum + 0x00
//   MIFARE Classic 1K/4K: block 4 (sector 1), key A FFFFFFFFFFFF
//   MIFARE Ultralight / NTAG21x: pages 4..7
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VDarts.Nfc
{
    public enum UidOrder { BigEndian, LittleEndian }

    public sealed class CardInfo
    {
        public string Reader;
        public byte[] Uid;
        public string UidHex;
        public string Kind;      // "MIFARE Classic 1K", "Ultralight/NTAG", ...
        public string Stored;    // 10 digits from a VDC1 record, or null
        public string Error;     // why the record could not be read, if any
    }

    public static class CardNumber
    {
        public static bool IsValid(string s)
        {
            if (s == null || s.Length != 10) return false;
            foreach (var c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        public static string FromUid(byte[] uid, UidOrder order)
        {
            if (uid == null || uid.Length == 0) return null;
            uint v = 0;
            int n = Math.Min(4, uid.Length);
            for (int i = 0; i < n; i++)
            {
                byte b = order == UidOrder.BigEndian ? uid[i] : uid[n - 1 - i];
                v = (v << 8) | b;
            }
            return v.ToString("D10");
        }

        public static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("X2"));
            return sb.ToString();
        }

        /// "UIDHEX=0123456789" lines; '#' comments. Missing file -> empty map.
        public static Dictionary<string, string> LoadMap(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (path == null || !File.Exists(path)) return map;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var uid = line.Substring(0, eq).Trim().Replace(":", "").Replace(" ", "");
                var num = line.Substring(eq + 1).Trim();
                if (IsValid(num)) map[uid] = num;
            }
            return map;
        }

        public static string Resolve(CardInfo c, Dictionary<string, string> map, UidOrder order)
        {
            if (IsValid(c.Stored)) return c.Stored;
            string m;
            if (map != null && map.TryGetValue(c.UidHex, out m)) return m;
            return FromUid(c.Uid, order);
        }

        internal static byte[] Record(string number)
        {
            var r = new byte[16];
            Encoding.ASCII.GetBytes("VDC1").CopyTo(r, 0);
            Encoding.ASCII.GetBytes(number).CopyTo(r, 4);
            r[14] = Sum(r);
            return r;
        }

        internal static string ParseRecord(byte[] r)
        {
            if (r == null || r.Length < 16) return null;
            if (r[0] != 'V' || r[1] != 'D' || r[2] != 'C' || r[3] != '1') return null;
            if (r[14] != Sum(r)) return null;
            var s = Encoding.ASCII.GetString(r, 4, 10);
            return IsValid(s) ? s : null;
        }

        static byte Sum(byte[] r)
        {
            byte s = 0x5A;
            for (int i = 0; i < 14; i++) s ^= r[i];
            return s;
        }
    }

    /// Polls a PC/SC reader on a background thread. Events fire on that thread.
    public sealed class PcscReader : IDisposable
    {
        public event Action<CardInfo> CardPresented;
        public event Action CardRemoved;
        public event Action<string> StatusChanged;

        public string Status { get; private set; }
        public string ReaderName { get; private set; }
        public CardInfo Current { get; private set; }

        readonly string _filter;
        readonly Thread _thread;
        volatile bool _stop;
        IntPtr _ctx;

        sealed class WriteJob
        {
            public string Number;
            public string Result;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }
        WriteJob _job;

        /// readerFilter: substring of the reader name to use ("" = first reader).
        public PcscReader(string readerFilter)
        {
            _filter = readerFilter ?? "";
            _thread = new Thread(Loop) { IsBackground = true, Name = "VDarts NFC" };
            _thread.Start();
        }

        /// Writes a 10-digit number to the card on the reader (waits up to timeoutMs
        /// for one to be presented). Returns null on success, else an error message.
        public string Write(string number, int timeoutMs)
        {
            if (!CardNumber.IsValid(number)) return "card number must be exactly 10 digits";
            var job = new WriteJob { Number = number };
            _job = job;
            if (!job.Done.WaitOne(timeoutMs))
            {
                Interlocked.CompareExchange(ref _job, null, job);
                return "no card presented";
            }
            return job.Result;
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(2000);
        }

        void SetStatus(string s)
        {
            if (s == Status) return;
            Status = s;
            var h = StatusChanged; if (h != null) h(s);
        }

        void Loop()
        {
            bool present = false;
            while (!_stop)
            {
                try
                {
                    if (_ctx == IntPtr.Zero)
                    {
                        int rc = SCardEstablishContext(SCARD_SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out _ctx);
                        if (rc != 0) { _ctx = IntPtr.Zero; SetStatus("PC/SC unavailable (" + Err(rc) + ")"); Sleep(3000); continue; }
                    }
                    string reader = FindReader();
                    if (reader == null)
                    {
                        ReaderName = null;
                        if (present) { present = false; Removed(); }
                        SetStatus("no NFC reader");
                        Sleep(1500);
                        continue;
                    }
                    ReaderName = reader;
                    IntPtr card; uint proto;
                    int c = SCardConnect(_ctx, reader, SCARD_SHARE_SHARED, SCARD_PROTOCOL_T0 | SCARD_PROTOCOL_T1, out card, out proto);
                    if (c != 0)
                    {
                        if (IsContextGone(c)) { Release(); continue; }
                        if (present) { present = false; Removed(); }
                        SetStatus("ready: " + reader);
                        Sleep(250);
                        continue;
                    }
                    try
                    {
                        var job = _job;
                        if (job != null)
                        {
                            job.Result = DoWrite(card, proto, job.Number);
                            _job = null;
                            job.Done.Set();
                            present = false; // re-read and re-announce the card
                        }
                        if (!present)
                        {
                            var info = ReadCard(card, proto, reader);
                            if (info != null)
                            {
                                present = true;
                                Current = info;
                                SetStatus("card " + info.UidHex + " on " + reader);
                                var h = CardPresented; if (h != null) h(info);
                            }
                        }
                    }
                    finally { SCardDisconnect(card, SCARD_LEAVE_CARD); }
                    Sleep(250);
                }
                catch (Exception ex)
                {
                    SetStatus("NFC error: " + ex.Message);
                    Sleep(2000);
                }
            }
            Release();
        }

        void Removed()
        {
            Current = null;
            var h = CardRemoved; if (h != null) h();
        }

        void Sleep(int ms)
        {
            for (int t = 0; t < ms && !_stop; t += 50) Thread.Sleep(50);
        }

        void Release()
        {
            if (_ctx != IntPtr.Zero) SCardReleaseContext(_ctx);
            _ctx = IntPtr.Zero;
        }

        static bool IsContextGone(int rc)
        {
            uint u = unchecked((uint)rc);
            return u == 0x80100003 || u == 0x8010001D || u == 0x8010001E || u == 0x80100006;
        }

        string FindReader()
        {
            uint len = 0;
            int rc = SCardListReaders(_ctx, null, null, ref len);
            if (rc != 0)
            {
                if (IsContextGone(rc)) Release();
                return null;
            }
            var buf = new char[len];
            if (SCardListReaders(_ctx, null, buf, ref len) != 0) return null;
            foreach (var r in new string(buf, 0, (int)len).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // ACR122U and friends expose a SAM slot too; skip it.
                if (r.IndexOf("SAM", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (_filter.Length == 0 || r.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0) return r;
            }
            return null;
        }

        // ---- card operations (PC/SC part 3 pseudo-APDUs, as ACR122U / ACR1252U etc. implement) ----

        static CardInfo ReadCard(IntPtr card, uint proto, string reader)
        {
            byte[] uid = Apdu(card, proto, new byte[] { 0xFF, 0xCA, 0x00, 0x00, 0x00 });
            if (uid == null || uid.Length == 0) return null;
            var info = new CardInfo { Reader = reader, Uid = uid, UidHex = CardNumber.Hex(uid), Kind = Kind(card) };
            byte[] rec = ReadRecord(card, proto, info.Kind);
            if (rec != null) info.Stored = CardNumber.ParseRecord(rec);
            else info.Error = "data area not readable";
            return info;
        }

        static bool IsClassic(string kind) { return kind.StartsWith("MIFARE Classic", StringComparison.Ordinal); }

        static byte[] ReadRecord(IntPtr card, uint proto, string kind)
        {
            if (IsClassic(kind))
            {
                if (!AuthClassic(card, proto)) return null;
                return Apdu(card, proto, new byte[] { 0xFF, 0xB0, 0x00, 0x04, 0x10 });
            }
            // Ultralight/NTAG: one READ returns 4 pages (16 bytes) starting at page 4.
            var r = Apdu(card, proto, new byte[] { 0xFF, 0xB0, 0x00, 0x04, 0x10 });
            if (r != null && r.Length >= 16) return r;
            if (kind == "unknown" && AuthClassic(card, proto))
                return Apdu(card, proto, new byte[] { 0xFF, 0xB0, 0x00, 0x04, 0x10 });
            return null;
        }

        static string DoWrite(IntPtr card, uint proto, string number)
        {
            string kind = Kind(card);
            byte[] rec = CardNumber.Record(number);
            bool ok;
            if (IsClassic(kind) || (kind == "unknown" && AuthClassic(card, proto)))
            {
                if (IsClassic(kind) && !AuthClassic(card, proto)) return "authentication failed (sector 1 key A is not FFFFFFFFFFFF)";
                var apdu = new byte[5 + 16];
                apdu[0] = 0xFF; apdu[1] = 0xD6; apdu[2] = 0x00; apdu[3] = 0x04; apdu[4] = 0x10;
                Array.Copy(rec, 0, apdu, 5, 16);
                ok = Apdu(card, proto, apdu) != null;
            }
            else
            {
                ok = true;
                for (int p = 0; p < 4 && ok; p++)
                {
                    var apdu = new byte[] { 0xFF, 0xD6, 0x00, (byte)(4 + p), 0x04, rec[p * 4], rec[p * 4 + 1], rec[p * 4 + 2], rec[p * 4 + 3] };
                    ok = Apdu(card, proto, apdu) != null;
                }
            }
            if (!ok) return "write rejected by card (" + kind + ", maybe locked)";
            var back = ReadRecord(card, proto, kind);
            return CardNumber.ParseRecord(back) == number ? null : "verify after write failed";
        }

        static bool AuthClassic(IntPtr card, uint proto)
        {
            // Load key FFFFFFFFFFFF into volatile slot 0, then authenticate block 4 with key A.
            if (Apdu(card, proto, new byte[] { 0xFF, 0x82, 0x00, 0x00, 0x06, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }) == null) return false;
            return Apdu(card, proto, new byte[] { 0xFF, 0x86, 0x00, 0x00, 0x05, 0x01, 0x00, 0x04, 0x60, 0x00 }) != null;
        }

        static string Kind(IntPtr card)
        {
            var atr = new byte[64];
            int len = atr.Length;
            if (SCardGetAttrib(card, SCARD_ATTR_ATR_STRING, atr, ref len) != 0 || len < 15) return "unknown";
            // PC/SC part 3 storage-card ATR: 3B 8n 80 01 80 4F 0C A0 00 00 03 06 SS NN NN ...
            if (atr[4] != 0x80 || atr[5] != 0x4F) return "unknown";
            int name = (atr[13] << 8) | atr[14];
            switch (name)
            {
                case 0x0001: return "MIFARE Classic 1K";
                case 0x0002: return "MIFARE Classic 4K";
                case 0x0026: return "MIFARE Classic Mini";
                case 0x0003: return "Ultralight/NTAG";
                default: return "unknown";
            }
        }

        /// Returns response data without SW1SW2, or null unless SW = 90 00.
        static byte[] Apdu(IntPtr card, uint proto, byte[] cmd)
        {
            var pci = new IoRequest { Protocol = proto, PciLength = 8 };
            var rx = new byte[258];
            int rxLen = rx.Length;
            if (SCardTransmit(card, ref pci, cmd, cmd.Length, IntPtr.Zero, rx, ref rxLen) != 0 || rxLen < 2) return null;
            if (rx[rxLen - 2] != 0x90 || rx[rxLen - 1] != 0x00) return null;
            var data = new byte[rxLen - 2];
            Array.Copy(rx, data, data.Length);
            return data;
        }

        static string Err(int rc) { return "0x" + unchecked((uint)rc).ToString("X8"); }

        // ---- winscard ----
        const uint SCARD_SCOPE_USER = 0;
        const uint SCARD_SHARE_SHARED = 2;
        const uint SCARD_PROTOCOL_T0 = 1, SCARD_PROTOCOL_T1 = 2;
        const uint SCARD_LEAVE_CARD = 0;
        const uint SCARD_ATTR_ATR_STRING = 0x00090303;

        [StructLayout(LayoutKind.Sequential)]
        struct IoRequest { public uint Protocol; public uint PciLength; }

        [DllImport("winscard.dll")]
        static extern int SCardEstablishContext(uint scope, IntPtr r1, IntPtr r2, out IntPtr ctx);
        [DllImport("winscard.dll")]
        static extern int SCardReleaseContext(IntPtr ctx);
        [DllImport("winscard.dll", EntryPoint = "SCardListReadersW", CharSet = CharSet.Unicode)]
        static extern int SCardListReaders(IntPtr ctx, string groups, [Out] char[] readers, ref uint len);
        [DllImport("winscard.dll", EntryPoint = "SCardConnectW", CharSet = CharSet.Unicode)]
        static extern int SCardConnect(IntPtr ctx, string reader, uint share, uint protocols, out IntPtr card, out uint active);
        [DllImport("winscard.dll")]
        static extern int SCardDisconnect(IntPtr card, uint disposition);
        [DllImport("winscard.dll")]
        static extern int SCardTransmit(IntPtr card, ref IoRequest sendPci, byte[] send, int sendLen, IntPtr recvPci, byte[] recv, ref int recvLen);
        [DllImport("winscard.dll")]
        static extern int SCardGetAttrib(IntPtr card, uint attr, byte[] buf, ref int len);
    }
}
