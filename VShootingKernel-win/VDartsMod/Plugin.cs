// VDartsMod - BepInEx 5 plugin for VDartsH2_V1.
//
//  * Cabinet buttons on configurable keys (default: arrow keys + Enter). Replaces the
//    game's built-in debug handler `keyboard.Update` so keys are not sent twice.
//  * NFC member / GameKey cards from a PC/SC reader (ACR122U etc.). The game reads
//    cards as 10 typed digits through Input.inputString; the plugin feeds the card
//    number in there for exactly one frame, so every login screen works unchanged.
//  * Card numbers can also come over UDP 127.0.0.1:<port> ("CARD 0123456789"), which
//    is what the board emulator's "virtual card" uses, or from hotkeys.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using VDarts.Nfc;

namespace VDartsMod
{
    [BepInPlugin("vdarts.mod", "VDarts Mod", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        // Buttons
        ConfigEntry<bool> _buttons, _debugKeys;
        ConfigEntry<KeyboardShortcut> _up, _down, _left, _right, _ok, _ok2, _coin, _miss;
        // Cards
        ConfigEntry<bool> _nfc;
        ConfigEntry<string> _readerFilter, _testCard, _operatorCard;
        ConfigEntry<UidOrder> _uidOrder;
        ConfigEntry<int> _udpPort;
        ConfigEntry<KeyboardShortcut> _operatorKey, _testKey;

        PcscReader _reader;
        UdpClient _udp;
        readonly Queue<string> _cards = new Queue<string>();
        string _mapPath;
        string _scoreBuffer = "";

        static string s_inject;
        static int s_injectFrame = -1;
        static bool s_hookWorks;

        void Awake()
        {
            Log = Logger;
            _buttons = Config.Bind("Buttons", "Enabled", true, "Map keyboard keys to the cabinet buttons (replaces the game's own debug keyboard handler).");
            _up = Config.Bind("Buttons", "Up", new KeyboardShortcut(KeyCode.UpArrow), "Cabinet Up (game code 83)");
            _down = Config.Bind("Buttons", "Down", new KeyboardShortcut(KeyCode.DownArrow), "Cabinet Down (84)");
            _left = Config.Bind("Buttons", "Left", new KeyboardShortcut(KeyCode.LeftArrow), "Cabinet Left (85)");
            _right = Config.Bind("Buttons", "Right", new KeyboardShortcut(KeyCode.RightArrow), "Cabinet Right (86)");
            _ok = Config.Bind("Buttons", "OK", new KeyboardShortcut(KeyCode.Return), "Cabinet OK (87)");
            _ok2 = Config.Bind("Buttons", "OK (alt)", new KeyboardShortcut(KeyCode.KeypadEnter), "Second key for OK");
            _coin = Config.Bind("Buttons", "Coin", new KeyboardShortcut(KeyCode.C), "Insert one credit (the original debug key)");
            _miss = Config.Bind("Buttons", "Miss", new KeyboardShortcut(KeyCode.O), "Dart missed the board (code 0, the original debug key)");
            _debugKeys = Config.Bind("Buttons", "DebugScoreEntry", true,
                "Keep the original debug dart entry: type a 3-digit code then I (1xx single, 2xx double, 3xx triple, 4xx inner single, 125 bull, 225 double bull). \\ or Delete clears.");

            _nfc = Config.Bind("Cards", "NfcReader", true, "Read member / GameKey cards from a PC/SC NFC reader.");
            _readerFilter = Config.Bind("Cards", "ReaderName", "", "Part of the reader name to use (empty = first reader found).");
            _uidOrder = Config.Bind("Cards", "UidByteOrder", UidOrder.BigEndian,
                "How a card with no written number turns its UID into 10 digits. Flip this if your existing cards' printed numbers do not match.");
            _udpPort = Config.Bind("Cards", "UdpPort", 47851, "Local UDP port for virtual cards from the board emulator (0 = off). Only listens on 127.0.0.1.");
            _operatorKey = Config.Bind("Cards", "OperatorCardKey", new KeyboardShortcut(KeyCode.F12), "Tap the operator (GameKey) card. Use it on the main menu's Setting item to open the operator menu.");
            _operatorCard = Config.Bind("Cards", "OperatorCardNumber", "2513081404", "Card number sent by OperatorCardKey. 2513081404 / 1762817609 are the staff cards built into the game.");
            _testKey = Config.Bind("Cards", "TestCardKey", new KeyboardShortcut(KeyCode.F11), "Tap the test member card.");
            _testCard = Config.Bind("Cards", "TestCardNumber", "", "Card number sent by TestCardKey (10 digits).");
            _mapPath = System.IO.Path.Combine(Paths.ConfigPath, "VDartsMod.cards.txt");

            var harmony = new Harmony("vdarts.mod");
            if (_buttons.Value)
            {
                var kbUpdate = AccessTools.Method(typeof(keyboard), "Update");
                harmony.Patch(kbUpdate, prefix: new HarmonyMethod(typeof(Plugin), nameof(SkipOriginal)));
                Log.LogInfo("Cabinet buttons: " + _up.Value + "/" + _down.Value + "/" + _left.Value + "/" + _right.Value + ", OK " + _ok.Value);
            }

            try
            {
                harmony.Patch(AccessTools.PropertyGetter(typeof(Input), "inputString"),
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(InputStringPostfix)));
                s_inject = "\u0001"; s_injectFrame = Time.frameCount;
                s_hookWorks = (Input.inputString ?? "").EndsWith("\u0001");
                s_inject = null; s_injectFrame = -1;
            }
            catch (Exception ex) { Log.LogWarning("Input.inputString hook failed: " + ex.Message); }
            Log.LogInfo(s_hookWorks ? "Card input: Input.inputString hook active"
                                    : "Card input: hook unavailable, typing digits with SendInput (game window must have focus)");

            if (_nfc.Value)
            {
                _reader = new PcscReader(_readerFilter.Value);
                _reader.StatusChanged += s => Log.LogInfo("NFC: " + s);
                _reader.CardPresented += OnCard;
            }
            if (_udpPort.Value > 0) StartUdp(_udpPort.Value);
        }

        void OnCard(CardInfo c)
        {
            // reader thread
            var map = CardNumber.LoadMap(_mapPath);
            string num = CardNumber.Resolve(c, map, _uidOrder.Value);
            string src = CardNumber.IsValid(c.Stored) ? "written on card" : map.ContainsKey(c.UidHex) ? "card map" : "UID " + _uidOrder.Value;
            Log.LogInfo("NFC card " + c.UidHex + " (" + c.Kind + ") -> " + num + " [" + src + "]; UID as BigEndian=" +
                        CardNumber.FromUid(c.Uid, UidOrder.BigEndian) + " LittleEndian=" + CardNumber.FromUid(c.Uid, UidOrder.LittleEndian));
            Enqueue(num);
        }

        void Enqueue(string num)
        {
            if (!CardNumber.IsValid(num)) { Log.LogWarning("ignored card number '" + num + "' (need 10 digits)"); return; }
            lock (_cards) _cards.Enqueue(num);
        }

        void StartUdp(int port)
        {
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                new Thread(() =>
                {
                    var any = new IPEndPoint(IPAddress.Any, 0);
                    while (true)
                    {
                        byte[] b;
                        try { b = _udp.Receive(ref any); } catch { return; }
                        var msg = Encoding.ASCII.GetString(b).Trim();
                        if (msg.StartsWith("CARD ", StringComparison.OrdinalIgnoreCase))
                        {
                            Log.LogInfo("Virtual card " + msg.Substring(5).Trim());
                            Enqueue(msg.Substring(5).Trim());
                        }
                    }
                }) { IsBackground = true, Name = "VDarts UDP" }.Start();
                Log.LogInfo("Virtual cards on udp://127.0.0.1:" + port);
            }
            catch (Exception ex) { Log.LogWarning("UDP port " + port + ": " + ex.Message); }
        }

        void Update()
        {
            if (_buttons.Value && !LockGame.LOCKKEYBOARD) Buttons();

            if (_operatorKey.Value.IsDown()) { Log.LogInfo("Operator card key"); Enqueue(_operatorCard.Value); }
            if (_testKey.Value.IsDown()) { Log.LogInfo("Test card key"); Enqueue(_testCard.Value); }

            if (Time.frameCount > s_injectFrame)
            {
                string next = null;
                lock (_cards) if (_cards.Count > 0) next = _cards.Dequeue();
                if (next != null) Inject(next);
            }
        }

        void Buttons()
        {
            if (_up.Value.IsDown()) Message.Recv = 83;
            else if (_down.Value.IsDown()) Message.Recv = 84;
            else if (_left.Value.IsDown()) Message.Recv = 85;
            else if (_right.Value.IsDown()) Message.Recv = 86;
            else if (_ok.Value.IsDown() || _ok2.Value.IsDown()) Message.Recv = 87;
            else if (_coin.Value.IsDown()) Money.Insert(1);
            else if (_miss.Value.IsDown()) Message.Recv = 0;
            else if (_debugKeys.Value) DebugScore();
        }

        // Port of the game's keyboard.cs dart code entry.
        void DebugScore()
        {
            for (int d = 0; d <= 9; d++)
                if (Input.GetKeyDown(KeyCode.Alpha0 + d)) { _scoreBuffer += (char)('0' + d); return; }
            if (Input.GetKeyDown(KeyCode.Backslash) || Input.GetKeyDown(KeyCode.Delete)) { _scoreBuffer = ""; return; }
            if (!Input.GetKeyDown(KeyCode.I) || _scoreBuffer.Length < 3) return;
            _scoreBuffer = _scoreBuffer.Substring(_scoreBuffer.Length - 3);
            int r; int.TryParse(_scoreBuffer, out r);
            if (r == 125) { Message.Recv = 81; return; }
            if (r == 225) { Message.Recv = 82; return; }
            switch (r / 100)
            {
                case 1: Message.Recv = r - 100; break;
                case 2: Message.Recv = 60 + r - 200; break;
                case 3: Message.Recv = 20 + r - 300; break;
                case 4: Message.Recv = 40 + r - 400; break;
            }
        }

        void Inject(string digits)
        {
            Log.LogInfo("Card " + digits + " -> game");
            if (s_hookWorks)
            {
                // Every reader of Input.inputString sees the digits during the next frame only,
                // exactly like a keyboard-wedge reader typing them.
                s_inject = digits;
                s_injectFrame = Time.frameCount + 1;
            }
            else
            {
                if (!Application.isFocused) Log.LogWarning("game window not focused; the card digits may go elsewhere");
                foreach (char ch in digits) TypeKey((byte)ch);
                s_injectFrame = Time.frameCount + 1;
            }
        }

        static bool SkipOriginal() { return false; }

        static void InputStringPostfix(ref string __result)
        {
            if (s_inject != null && Time.frameCount == s_injectFrame)
                __result = (__result ?? "") + s_inject;
        }

        void OnDestroy()
        {
            if (_reader != null) _reader.Dispose();
            if (_udp != null) _udp.Close();
        }

        [DllImport("user32.dll")]
        static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        static void TypeKey(byte vk)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 2, UIntPtr.Zero);
        }
    }
}
