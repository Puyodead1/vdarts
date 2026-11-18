using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace VDartsServerRedirect
{
    /// <summary>
    /// Redirects every HTTP(S) request the game makes to the official VDarts servers
    /// (*.vdarts.net, *.vdarts.tw) to a self-hosted vdarts-server, and (optionally) the online
    /// lobby's raw TCP connection (ServerIpControl.ONLINE_IP/ONLINE_PORT) as well.
    ///
    /// The game talks to its backend through three different stacks, all hooked here:
    ///   * UnityEngine.WWW            (~95 call sites, GameKey, PlayerInfoPool, rankings, business...)
    ///   * UnityEngine.Networking.UnityWebRequest (a few downloads)
    ///   * System.Net.WebRequest.Create (APICore: the /MemberAPI/*.php layer incl. card login)
    /// Additionally the static URL fields of ServerIpControl are rewritten at start so any
    /// code that only *displays* or concatenates them sees the new server too.
    /// Path components are preserved, so one server must serve /MemberAPI, /VDartsAPIs,
    /// /MemberPlatformWS, /picture, /download ... (vdarts-server does).
    /// </summary>
    [BepInPlugin(Guid, "VDarts Server Redirect", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "net.vdarts.preservation.serverredirect";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> BaseUrl;
        internal static ConfigEntry<string> Domains;
        internal static ConfigEntry<bool> LogRequests;
        internal static ConfigEntry<string> MachineMac;
        internal static ConfigEntry<bool> LobbyEnabled;
        internal static ConfigEntry<string> LobbyHost;
        internal static ConfigEntry<int> LobbyPort;

        private static Regex _hostRegex;
        private static string _base; // e.g. "http://127.0.0.1:8090" (no trailing slash)

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("Server", "Enabled", true, "Redirect VDarts cloud requests to BaseUrl.");
            BaseUrl = Config.Bind("Server", "BaseUrl", "http://127.0.0.1:8090",
                "Scheme://host[:port] of your vdarts-server. The original path (/MemberAPI/..., /VDartsAPIs/...) is appended.");
            Domains = Config.Bind("Server", "Domains", "vdarts.net,vdarts.tw",
                "Comma separated domains whose hosts (and subdomains) are redirected.");
            LogRequests = Config.Bind("Server", "LogRequests", true, "Log every rewritten URL to the BepInEx console/log.");
            MachineMac = Config.Bind("Machine", "MacOverride", "",
                "Optional: force ServerIpControl.MACHINE_MAC (machine id sent to the server). Empty = game default.");

            LobbyEnabled = Config.Bind("Lobby", "Enabled", true, "Point the online lobby (raw TCP, originally online.vdarts.net:8008) at your server.");
            LobbyHost = Config.Bind("Lobby", "Host", "", "Lobby host name or IP. Empty = the host part of [Server] BaseUrl.");
            LobbyPort = Config.Bind("Lobby", "Port", 8008, "Lobby TCP port (vdarts-server option Vdarts:LobbyPort).");

            if (!Enabled.Value) { Log.LogInfo("Disabled by config."); return; }

            _base = BaseUrl.Value.Trim().TrimEnd('/');
            var doms = Domains.Value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(d => Regex.Escape(d.Trim().TrimStart('.')));
            // scheme://[sub.]domain[:port]  — followed by '/', '?', '#' or end
            _hostRegex = new Regex(@"^\s*https?://([A-Za-z0-9\-]+\.)*(" + string.Join("|", doms.ToArray()) + @")(:\d+)?(?=[/?#]|$)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

            RewriteStaticFields();

            var h = new Harmony(Guid);
            PatchAll(h);
            Log.LogInfo("Redirecting VDarts cloud -> " + _base);
        }

        internal static string Rewrite(string url)
        {
            if (string.IsNullOrEmpty(url) || _hostRegex == null) return url;
            var m = _hostRegex.Match(url);
            if (!m.Success) return url;
            var res = _base + url.Substring(m.Length);
            if (LogRequests.Value) Log.LogInfo("HTTP " + url + "  =>  " + res);
            return res;
        }

        internal static Uri Rewrite(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri) return uri;
            var s = uri.OriginalString;
            var r = Rewrite(s);
            return ReferenceEquals(r, s) || r == s ? uri : new Uri(r);
        }

        private static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(name, false); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        private void RewriteStaticFields()
        {
            var t = FindType("ServerIpControl");
            if (t == null) { Log.LogWarning("ServerIpControl not found"); return; }
            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.FieldType != typeof(string) || f.IsLiteral || f.IsInitOnly) continue;
                var v = (string)f.GetValue(null);
                var nv = Rewrite(v);
                if (nv != v) { f.SetValue(null, nv); }
            }
            // Online lobby ("remote PK"): raw TCP, ServerIpControl.ONLINE_IP / ONLINE_PORT (online.vdarts.net:8008).
            if (LobbyEnabled.Value)
            {
                var host = LobbyHost.Value.Trim();
                if (host == "")
                {
                    try { host = new Uri(_base).Host; } catch { host = "127.0.0.1"; }
                }
                var ip = t.GetField("ONLINE_IP", BindingFlags.Static | BindingFlags.Public);
                var port = t.GetField("ONLINE_PORT", BindingFlags.Static | BindingFlags.Public);
                if (ip != null && port != null)
                {
                    var old = ip.GetValue(null) + ":" + port.GetValue(null);
                    ip.SetValue(null, host);
                    port.SetValue(null, LobbyPort.Value.ToString());
                    Log.LogInfo("Lobby " + old + "  =>  " + host + ":" + LobbyPort.Value);
                }
                else Log.LogWarning("ServerIpControl.ONLINE_IP/ONLINE_PORT not found");
            }
            if (!string.IsNullOrEmpty(MachineMac.Value))
            {
                var mac = t.GetField("MACHINE_MAC", BindingFlags.Static | BindingFlags.Public);
                if (mac != null) mac.SetValue(null, MachineMac.Value);
            }
        }

        private static void PatchAll(Harmony h)
        {
            var strPrefix = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(UrlArg0Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            var uriPrefix = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(UriArg0Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            var targets = new List<MethodBase>();

            // UnityEngine.WWW constructors (first parameter is the url string)
            var www = FindType("UnityEngine.WWW");
            if (www != null)
                targets.AddRange(www.GetConstructors().Where(c => FirstParamIs(c, typeof(string))).Cast<MethodBase>());
            // WWW.LoadFromCacheOrDownload(string url, ...)
            if (www != null)
                targets.AddRange(www.GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .Where(m => m.Name == "LoadFromCacheOrDownload" && FirstParamIs(m, typeof(string))).Cast<MethodBase>());

            // UnityWebRequest: constructors, url setter, static helpers
            var uwr = FindType("UnityEngine.Networking.UnityWebRequest");
            if (uwr != null)
            {
                targets.AddRange(uwr.GetConstructors().Where(c => FirstParamIs(c, typeof(string))).Cast<MethodBase>());
                var setUrl = uwr.GetProperty("url")?.GetSetMethod();
                if (setUrl != null) targets.Add(setUrl);
                targets.AddRange(uwr.GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .Where(m => (m.Name == "Get" || m.Name == "Post" || m.Name == "Put" || m.Name == "Head" || m.Name == "Delete")
                                && FirstParamIs(m, typeof(string))).Cast<MethodBase>());
            }

            // System.Net.WebRequest.Create / CreateHttp (string)
            targets.AddRange(typeof(System.Net.WebRequest).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(m => (m.Name == "Create" || m.Name == "CreateHttp" || m.Name == "CreateDefault") && FirstParamIs(m, typeof(string))).Cast<MethodBase>());

            foreach (var t in targets.Distinct())
            {
                try { h.Patch(t, prefix: strPrefix); }
                catch (Exception e) { Log.LogWarning("Patch failed " + t.DeclaringType + "." + t.Name + ": " + e.Message); }
            }

            var uriTargets = new List<MethodBase>();
            if (uwr != null)
            {
                uriTargets.AddRange(uwr.GetConstructors().Where(c => FirstParamIs(c, typeof(Uri))).Cast<MethodBase>());
                var setUri = uwr.GetProperty("uri")?.GetSetMethod();
                if (setUri != null) uriTargets.Add(setUri);
            }
            uriTargets.AddRange(typeof(System.Net.WebRequest).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(m => (m.Name == "Create" || m.Name == "CreateHttp" || m.Name == "CreateDefault") && FirstParamIs(m, typeof(Uri))).Cast<MethodBase>());
            foreach (var t in uriTargets.Distinct())
            {
                try { h.Patch(t, prefix: uriPrefix); }
                catch (Exception e) { Log.LogWarning("Patch failed " + t.DeclaringType + "." + t.Name + ": " + e.Message); }
            }
            Log.LogInfo("Patched " + (targets.Count + uriTargets.Count) + " HTTP entry points");
        }

        private static bool FirstParamIs(MethodBase m, Type t)
        {
            var p = m.GetParameters();
            return p.Length > 0 && p[0].ParameterType == t;
        }

        // Harmony binds "__0" to the first argument by position (works for ctors, setters and statics).
        private static void UrlArg0Prefix(ref string __0) { __0 = Rewrite(__0); }
        private static void UriArg0Prefix(ref Uri __0) { __0 = Rewrite(__0); }
    }
}
