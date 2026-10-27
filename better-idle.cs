// Better Idle tray: keeps the idle hooks inside Discord and Steam.
// Build: %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /out:better-idle.exe /r:System.Web.Extensions.dll better-idle.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

static class BetterIdle
{
    // page script; double every " when editing it
    const string DiscordIdle = @"// Discord page script: stay Idle until you do something other people can see.
(() => {
    if (window.__discordAfk) return;
    window.__discordAfk = true;

    const TIMEOUT = 10 * 60 * 1000;  // Discord's own idle threshold
    // API calls others can see, as ""METHOD /path"" without the /api/vN prefix
    const VISIBLE_API = [
        /^POST \/channels\/\d+\/(messages|typing|threads|send-soundboard-sound)$/,
        /^(PATCH|DELETE) \/channels\/\d+\/messages\/\d+$/,
        /^POST \/channels\/\d+\/messages\/\d+\/(threads|crosspost)$/,
        /^(PUT|DELETE) \/channels\/\d+\/messages\/\d+\/reactions\/.+$/,
        /^(PUT|DELETE) \/channels\/\d+\/messages\/pins\/\d+$/,
        /^(PUT|DELETE) \/channels\/\d+\/polls\/\d+\/answers\/@me$/,
        /^POST \/interactions$/,
        /^POST \/invites\/[^/]+$/,
        /^(PUT|PATCH) \/guilds\/\d+\/(members|profile)\/@me(\/nick)?$/,  // server profile; @me arrives as %40me
        /^(POST|PUT|DELETE) \/users\/@me\/relationships(\/\d+)?$/,
        /^PATCH \/users\/@me(\/profile)?$/,
    ];
    // local typing and voice actions others can see; they only fire for you
    const VISIBLE_ACTIONS = [""TYPING_START_LOCAL"", ""VOICE_CHANNEL_SELECT"", ""AUDIO_TOGGLE_SELF_MUTE"",
        ""AUDIO_TOGGLE_SELF_DEAF"", ""STREAM_START"", ""MEDIA_ENGINE_SET_VIDEO_ENABLED""];

    let lastSeen = Date.now(), idle = false, flux, auth;  // launching Discord is visible too

    window.webpackChunkdiscord_app.push([[Symbol()], {}, r => {  // called once per bundler runtime
        const find = (marker, fn) => {
            const id = Object.keys(r.m).find(k => String(r.m[k]).includes(marker));
            return id && Object.values(r(id)).find(v => typeof v?.[fn] === ""function"");
        };
        auth ??= find('displayName=""AuthenticationStore""', ""getId"");
    }]);
    flux = auth._dispatcher;  // every Flux store holds it; the dispatcher has no stable marker

    const dispatch = flux.dispatch.bind(flux);
    const active = () => Date.now() - lastSeen < TIMEOUT;
    const seen = () => {
        lastSeen = Date.now();
        if (idle) setTimeout(() => flux.dispatch({ type: ""IDLE"", idle: false }));  // no nested dispatch
    };

    const open = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (method, url) {
        const u = new URL(url, location.href);
        const path = /(^|\.)discord\.com$/.test(u.hostname) && u.pathname.match(/^\/api\/v\d+(\/.*)$/)?.[1].replace(/%40/g, '@');  // other sites' XHRs pass through here too
        if (path && VISIBLE_API.some(re => re.test(`${String(method).toUpperCase()} ${path}`))) seen();
        return open.apply(this, arguments);
    };

    flux.dispatch = action => {
        if (action.type === ""IDLE"") {
            if (!action.idle && !active()) return Promise.resolve();  // clicks, focus, PC input
            idle = action.idle;
        } else if (action.type === ""LOGIN_SUCCESS"") {  // a switched-in account starts Online
            idle = false;
            lastSeen = Date.now();
        } else if (VISIBLE_ACTIONS.includes(action.type)
            || action.type === ""SPEAKING"" && action.speakingFlags && action.userId === auth?.getId()
            || action.type === ""USER_SETTINGS_PROTO_UPDATE"" && action.local
                && action.settings?.proto?.status?.status?.value === ""online"") {  // you picked Online here
            seen();
        }
        return dispatch(action);
    };

    setInterval(() => {
        if (!idle && !active()) flux.dispatch({ type: ""IDLE"", idle: true, idleSince: Date.now() });
    }, 10 * 1000);
})();
";

    // page script; double every " when editing it
    const string SteamIdle = @"// Steam page script: stay Away until you do something other people can see.
(() => {
    if (window.__steamAfk) return;

    const TIMEOUT = 5 * 60 * 1000;  // Steam's own Away threshold
    const OFFLINE = 0, ONLINE = 1, AWAY = 3;  // EPersonaState
    const PRIVATE = 1;  // EPrivacyState
    // unified service methods others can see, matched without the ""#1"" suffix
    const VISIBLE = [
        /^FriendMessages\.(SendMessage|UpdateMessageReaction)$/,  // SendMessage also carries ""is typing""
        /^ChatRoom\.(SendChatMessage|UpdateMessageReaction|DeleteChatMessages)$/,
        /^ChatRoom\.(Join|Leave)VoiceChat$/,
        /^ChatRoom\.(Create|Join|Leave)ChatRoomGroup$/,
        /^ChatRoom\.(InviteFriendToChatRoomGroup|CreateInviteLink)$/,
        /^VoiceChat\.(NotifyUserVoiceStatus|LeaveVoiceChat)$/,  // mic, mute and camera changes in a call
        /^Player\.(AddFriend|RemoveFriend|IgnoreFriend)$/,
        /^Player\.(PostStatusToFriends|DeletePostedStatus)$/,
        /^Player\.Set(AnimatedAvatar|AvatarFrame|ProfileBackground|ProfileTheme|MiniProfileBackground|FavoriteBadge)$/,
        /^Community\.(PostCommentToThread|DeleteCommentFromThread)$/,
        /^Clan\.RespondToClanInvite$/,
        /^Broadcast\.PostChatMessage$/,
    ];

    const app = window.g_FriendsUIApp;
    const store = app?.m_FriendStore;
    const transport = app?.m_CMInterface?.GetServiceTransport?.();
    if (!store?.UpdateUserPersonaStateInternal || !transport?.SendMsg) return;  // retried until the UI is up
    window.__steamAfk = true;

    let lastSeen = Date.now();  // connecting to Steam is visible too
    const active = () => Date.now() - lastSeen < TIMEOUT;

    // Invisible, Busy and Snooze are yours; we only ever move between Online and Away
    const ours = () => store.m_eUserPersonaState === ONLINE || store.m_eUserPersonaState === AWAY;
    // same call as Steam's idle timer, so Away isn't saved as your pick
    const set = state => {
        if (ours() && store.m_eUserPersonaState !== state)
            store.UpdateUserPersonaStateInternal(state, false, state === AWAY, false, ""BetterIdle"");
    };

    // webpack lookup by source marker, since module ids move between Steam updates
    let req;
    window.webpackChunksteamui?.push([[Symbol()], {}, r => { req ??= r; }]);
    const mod = test => {
        const id = req && Object.keys(req.m).find(k => test(String(req.m[k])));
        return id && req(id);
    };
    const one = (m, test) => m && Object.values(m).find(test);
    const msgs = mod(s => s.includes(""static Init("") && s.includes(""Body()"") && s.includes(""Hdr()""));
    const W = one(msgs, v => typeof v === ""function"" && typeof v.Init === ""function"");
    const ask = (marker, name) => {
        const m = mod(s => s.includes(marker));
        const svc = one(m, v => v && typeof v[name] === ""function"");
        const cls = one(m, v => typeof v === ""function"" && String(v).includes(name + ""_Request""));
        return svc && cls && W ? svc[name](transport, W.Init(cls)) : Promise.reject();
    };

    // a game counts only while others can see it: not marked private, profile not private
    let privateIds = null, gamesShown = false, lastAppId = 0;
    const refresh = async () => {
        try {
            const list = await ask(""AccountPrivateApps.GetPrivateAppList"", ""GetPrivateAppList"");
            const priv = await ask(""Player.GetPrivacySettings#1"", ""GetPrivacySettings"");
            const p = priv.Body().toObject().privacy_settings;
            gamesShown = p.privacy_state !== PRIVATE && p.privacy_state_ownedgames !== PRIVATE;
            privateIds = new Set(list.Body().toObject().private_apps.appids);
        } catch { privateIds = null; }  // can't tell, so it doesn't count
    };
    refresh();

    const inGame = () => {
        const id = store.self?.mutable_persona?.m_unGamePlayedAppID || 0;
        // privacy may have changed, and until the answer lands the game doesn't count
        if (id !== lastAppId) { lastAppId = id; if (id) { privateIds = null; refresh(); } }
        return id > 0 && gamesShown && privateIds !== null && !privateIds.has(id);
    };

    // every status change lands here, Steam's own idle handling included
    const update = store.UpdateUserPersonaStateInternal.bind(store);
    store.UpdateUserPersonaStateInternal = (state, userSet, idle, fromServer, why) => {
        if (!fromServer && state !== OFFLINE) {
            if (userSet && state === ONLINE) lastSeen = Date.now();  // you picked Online yourself
            else if (!userSet && ours()) {  // Steam's idle timer or input wake-up; our clock decides
                state = active() ? ONLINE : AWAY;
                idle = state === AWAY;
            }
        }
        return update(state, userSet, idle, fromServer, why);
    };

    for (const fn of [""SendMsg"", ""SendNotification""]) {
        const send = transport[fn];
        transport[fn] = function (name, ...rest) {
            if (VISIBLE.some(re => re.test(String(name).split(""#"")[0]))) {
                lastSeen = Date.now();  // set() alone no-ops once you are already Online
                set(ONLINE);
            }
            return send.call(this, name, ...rest);
        };
    }

    (window.SetBackgroundInterval || setInterval)(() => {
        if (inGame()) { lastSeen = Date.now(); set(ONLINE); }
        if (!active()) set(AWAY);
    }, 10 * 1000);
})();
";

    const string Version = "0.0.7";

    const string Tags = "https://api.github.com/repos/de18u/better-idle-releases/tags?per_page=100";  // ponytail: first 100 tags only
    const string Raw = "https://raw.githubusercontent.com/de18u/better-idle-releases/";

    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    // Squirrel falls back to ProgramData\<user> on some PCs
    static readonly string[] DiscordRoots = {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.UserName, "Discord"),
    };

    const string DiscordOff = "module.exports = require('./core.asar');";

    static readonly string DiscordOn =
        "require('electron').app.on('browser-window-created', (_, win) => win.webContents.on('did-finish-load', () => {\n" +
        "    if (win.webContents.getURL().startsWith('https://discord.com/'))\n" +
        "        win.webContents.executeJavaScript(" + Json.Serialize(DiscordIdle) + ").catch(console.error);\n" +
        "}));\n" + DiscordOff;

    static readonly string SteamRoot = FindSteam();
    static readonly string SteamMarker = Path.Combine(SteamRoot, ".cef-enable-remote-debugging");

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly string RunCommand = "\"" + Application.ExecutablePath + "\"";

    const string SettingsKey = @"Software\Better Idle";

    static NotifyIcon tray;
    static MenuItem discord, steam, startup;
    static System.Threading.Timer timer, updater;  // held so they are not collected
    static Mutex single;  // held so a second launch sees it

    static string FindSteam()
    {
        object p = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null);
        return p == null
            ? @"C:\Program Files (x86)\Steam"
            : Path.GetFullPath(((string)p).Replace('/', '\\'));
    }

    // amber crescent when either hook is on, grey when both are off
    static Icon MakeIcon(bool on)
    {
        Color c = on ? Color.FromArgb(240, 178, 50) : Color.FromArgb(114, 118, 125);
        Bitmap bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        using (Brush face = new SolidBrush(c))
        using (Brush hole = new SolidBrush(Color.FromArgb(0, c)))  // clear face colour, no white fringe
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(face, 2, 2, 28, 28);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.FillEllipse(hole, 13, -4, 26, 26);
        }
        Icon icon = Icon.FromHandle(bmp.GetHicon());
        bmp.Dispose();
        return icon;
    }

    static readonly Icon OnIcon = MakeIcon(true);
    static readonly Icon OffIcon = MakeIcon(false);

    static IEnumerable<string> Cores()
    {
        foreach (string root in DiscordRoots.Where(Directory.Exists))
        foreach (string app in Directory.GetDirectories(root, "app-*"))
        {
            string modules = Path.Combine(app, "modules");
            if (!Directory.Exists(modules)) continue;
            foreach (string core in Directory.GetDirectories(modules, "discord_desktop_core-*"))
            {
                string index = Path.Combine(core, "discord_desktop_core", "index.js");
                if (File.Exists(index)) yield return index;
            }
        }
    }

    static bool SyncDiscord()
    {
        string want = discord.Checked ? DiscordOn : DiscordOff;
        bool wrote = false;
        foreach (string index in Cores())
            try { if (File.ReadAllText(index) != want) { File.WriteAllText(index, want); wrote = true; } } catch { }
        return wrote;
    }

    static void SyncSteam()
    {
        try
        {
            if (steam.Checked && !File.Exists(SteamMarker)) File.WriteAllText(SteamMarker, "");
            else if (!steam.Checked && File.Exists(SteamMarker)) File.Delete(SteamMarker);
        }
        catch { }
    }

    // Steam serves the debugger on 8080 once the marker file is there
    static string SharedJSContext()
    {
        using (WebClient web = new WebClient())
        {
            object[] targets = (object[])Json.DeserializeObject(
                web.DownloadString("http://127.0.0.1:8080/json"));
            foreach (Dictionary<string, object> t in targets)
            {
                object title, url;
                if (t.TryGetValue("title", out title) && (string)title == "SharedJSContext"
                    && t.TryGetValue("webSocketDebuggerUrl", out url)) return (string)url;
            }
        }
        return null;
    }

    // the page script skips itself if it is already in, so this can just run again
    static void Inject()
    {
        try
        {
            string url = SharedJSContext();
            if (url == null) return;
            using (ClientWebSocket sock = new ClientWebSocket())
            {
                sock.ConnectAsync(new Uri(url), CancellationToken.None).Wait(5000);
                byte[] msg = Encoding.UTF8.GetBytes(
                    "{\"id\":1,\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":"
                    + Json.Serialize(SteamIdle) + "}}");
                sock.SendAsync(new ArraySegment<byte>(msg), WebSocketMessageType.Text, true,
                    CancellationToken.None).Wait(5000);
                sock.ReceiveAsync(new ArraySegment<byte>(new byte[8192]),
                    CancellationToken.None).Wait(5000);
            }
        }
        catch { }
    }

    // the hook only takes effect on a fresh Discord launch
    static void RestartDiscord()
    {
        Process[] all = Process.GetProcessesByName("Discord");
        if (all.Length == 0) return;
        string update = null;
        try
        {
            Process main = all.OrderBy(p => p.StartTime).First();  // the one that spawned the rest
            // the running install's Update.exe, one above app-*
            update = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(main.MainModule.FileName)), "Update.exe");
            main.Kill();
            main.WaitForExit(5000);
        }
        catch { }
        try { Process.Start(update, "--processStart Discord.exe"); } catch { }
    }

    static DateTime lastRelaunch = DateTime.MinValue;

    // an update on launch swaps index.js after Discord already loaded it
    static void RelaunchIfJustStarted()
    {
        try
        {
            Process[] all = Process.GetProcessesByName("Discord");
            if (all.Length == 0 || DateTime.Now - all.Min(p => p.StartTime) > TimeSpan.FromMinutes(2)) return;
        }
        catch { return; }
        if (DateTime.Now - lastRelaunch < TimeSpan.FromMinutes(10)) return;  // no loop if every launch swaps it
        lastRelaunch = DateTime.Now;
        RestartDiscord();
    }

    // the marker file only takes effect on a fresh Steam launch
    static void RestartSteam()
    {
        string exe = Path.Combine(SteamRoot, "steam.exe");
        if (Process.GetProcessesByName("steam").Length == 0 || !File.Exists(exe)) return;
        try
        {
            Process.Start(exe, "-shutdown");
            for (int i = 0; i < 30 && Process.GetProcessesByName("steam").Length > 0; i++)
                Thread.Sleep(500);
            Process.Start(exe);
        }
        catch { }
    }

    static void TryUpdate(SynchronizationContext ui)
    {
        string exe = Application.ExecutablePath, next = exe + ".new", old = exe + ".old", cs = next + ".cs";
        try
        {
            foreach (string f in new[] { old, next, cs }) File.Delete(f);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;  // csc builds offer only Ssl3 and Tls
            using (WebClient web = new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = "Better Idle";  // GitHub's API refuses requests without one
                string tag = ((object[])Json.DeserializeObject(web.DownloadString(Tags)))
                    .Select(t => (string)((Dictionary<string, object>)t)["name"])
                    .Where(n => Regex.IsMatch(n, @"^v\d+\.\d+\.\d+$"))
                    .OrderByDescending(n => new Version(n.Substring(1)))
                    .FirstOrDefault();
                if (tag == null || new Version(tag.Substring(1)) <= new Version(Version)) return;
                byte[] source = web.DownloadData(Raw + tag + "/better-idle.cs");
                if (!Encoding.UTF8.GetString(source).Contains("const string Version = \"" + tag.Substring(1) + "\";")) return;
                File.WriteAllBytes(cs, source);
            }

            ProcessStartInfo csc = new ProcessStartInfo(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "csc.exe"),
                "/nologo /target:winexe /r:System.Web.Extensions.dll /out:\"" + next + "\" \"" + cs + "\"");
            csc.UseShellExecute = false;
            csc.CreateNoWindow = true;
            int code;
            using (Process p = Process.Start(csc)) { p.WaitForExit(); code = p.ExitCode; }
            File.Delete(cs);
            if (code != 0 || !File.Exists(next)) return;

            File.Move(exe, old);  // Windows lets a running exe be renamed, not overwritten
            try { File.Move(next, exe); }
            catch { File.Move(old, exe); return; }
            ui.Post(delegate
            {
                single.ReleaseMutex();
                single.Close();  // the name stays taken until its last handle closes
                try { Process.Start(exe); }
                catch { single = new Mutex(true, "Better Idle"); return; }
                tray.Visible = false;
                Application.Exit();
            }, null);
        }
        catch { }
    }

    static bool StartsWithWindows()
    {
        using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey))
            return run != null && run.GetValue("Better Idle") as string == RunCommand;
    }

    static void ToggleStartup()
    {
        try
        {
            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey))
                if (startup.Checked) run.DeleteValue("Better Idle", false);
                else run.SetValue("Better Idle", RunCommand);
        }
        catch { }
        startup.Checked = StartsWithWindows();  // show what the registry really holds
    }

    // a tick nobody saved yet counts as on
    static bool Load(MenuItem m)
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey))
            return key == null || !Equals(key.GetValue(m.Text), 0);
    }

    static void Save(MenuItem m)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                key.SetValue(m.Text, m.Checked ? 1 : 0);
        }
        catch { }
    }

    // icon and hover text follow the Discord and Steam ticks
    static void ShowState()
    {
        string on = string.Join(", ", new[] { discord, steam }.Where(m => m.Checked).Select(m => m.Text));
        tray.Icon = on.Length > 0 ? OnIcon : OffIcon;
        tray.Text = "Better Idle: " + (on.Length > 0 ? on : "off");
    }

    static void Toggle(MenuItem m, Action sync, Action restart)
    {
        m.Checked = !m.Checked;
        Save(m);
        ShowState();
        sync();
        var ctx = SynchronizationContext.Current;  // let the menu close first
        if (ctx != null) ctx.Post(delegate { restart(); }, null); else restart();
    }

    [STAThread]
    static void Main()
    {
        bool first;
        single = new Mutex(true, "Better Idle", out first);
        if (!first) return;  // two copies would undo each other's ticks
        tray = new NotifyIcon();
        tray.ContextMenu = new ContextMenu();

        MenuItem name = tray.ContextMenu.MenuItems.Add("Better Idle " + Version);
        name.Enabled = false;  // a heading, not a command
        tray.ContextMenu.MenuItems.Add("-");
        discord = tray.ContextMenu.MenuItems.Add("Discord",
            delegate { Toggle(discord, () => SyncDiscord(), RestartDiscord); });
        steam = tray.ContextMenu.MenuItems.Add("Steam",
            delegate { Toggle(steam, SyncSteam, RestartSteam); });
        tray.ContextMenu.MenuItems.Add("-");
        startup = tray.ContextMenu.MenuItems.Add("Start with Windows", delegate { ToggleStartup(); });
        tray.ContextMenu.MenuItems.Add("Exit", delegate { tray.Visible = false; Application.Exit(); });
        discord.Checked = Load(discord);
        steam.Checked = Load(steam);
        startup.Checked = StartsWithWindows();
        ShowState();
        tray.Visible = true;

        SyncSteam();
        // Discord updates overwrite index.js, and Steam drops the script when it restarts
        timer = new System.Threading.Timer(delegate
        {
            if (SyncDiscord()) RelaunchIfJustStarted();
            if (steam.Checked) Inject();
        }, null, 0, 30000);
        SynchronizationContext ui = new WindowsFormsSynchronizationContext();  // this thread owns the mutex
        updater = new System.Threading.Timer(delegate { TryUpdate(ui); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
        Application.Run();
    }
}
