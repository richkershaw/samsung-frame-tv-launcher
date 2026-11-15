using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PopupNotify.Native;

// Native Samsung Tizen popup for the popup-notifications Shard.
//
// This is the drawing half. It is a resident process the TV's own signed
// /usr/bin/dotnet runs as root; it creates an EFL notification window and draws a
// card over whatever app is on screen. The window trick — ELM_WIN_NOTIFICATION, a
// notification level and a high window layer — is the launcher's, copied from
// dock/src/Program.cs in the Vetro source; a web app cannot do any of this, which
// is why a Shard needs a native helper for it.
//
// Lifecycle, like the launcher: the process stays resident, and "notify"/"hide"
// arrive over a FIFO so showing a popup costs a window map, not a dotnet cold start.
// Hidden, it sleeps in the EFL main loop with no timer and nothing on screen.
//
//   notify.dll --dir <workDir> --event-url <url> --event-token <token>
//   notify.dll --status  --dir <workDir>      (prints JSON, exits)
//   notify.dll --self-test                    (prints JSON, exits)
//
// The card, the action pills and the focus ring are rasterised here as raw
// premultiplied ARGB (the Dock does the same for icons), so the Shard bundle stays
// text-only: no PNG ships with it.
namespace PopupNotify
{
    internal static class Program
    {
        // What makes the window a system notification the compositor stacks on top.
        private const int WindowLayer = 900;
        private const int NotificationLevel = 40;

        private const int DefaultScreenW = 1920, DefaultScreenH = 1080;
        private const string Font = "SamsungOneUI 500,Sans";
        private const string FontBold = "SamsungOneUI 700,Sans";
        private const double FadeSeconds = 0.18;
        private const long LogLimit = 256 * 1024;

        // Base metrics at scale 1.0; everything is multiplied by the payload's sizeMultiplier.
        private const int BaseCardW = 560;
        private const int BasePad = 28;
        private const int BaseRadius = 24;
        private const int BaseBorder = 2;
        private const int BaseTitleSize = 26;
        private const int BaseMsgSize = 30;
        private const int BasePillH = 46;
        private const int BasePillPadX = 22;
        private const int BasePillGap = 12;
        private const int BasePillFont = 22;
        private const int BaseGap = 18;
        private const int BaseImageH = 260;
        private const int EdgeMargin = 56;
        private const int ShadowPad = 22;

        // Colours (0xRRGGBB); alpha is separate, premultiplied on the way into a pixel.
        private const int CardRgb = 0x202634;
        private const float CardAlpha = 0.97f;
        private const int CardBorderRgb = 0xFFFFFF;
        private const float CardBorderAlpha = 0.16f;
        private const float ShadowAlpha = 0.42f;
        private const float PillAlpha = 0.14f;

        private static readonly Efl.TaskCb AnimCb = OnFrame;
        private static readonly Efl.TaskCb TimeoutCb = OnTimeout;
        private static readonly Efl.TaskCb FadeOutCb = OnFadeOut;
        private static readonly Efl.FdCb FifoCb = OnFifo;
        private static readonly Efl.FdCb InputCb = OnInput;
        private static readonly Efl.TaskCb RescanCb = OnRescan;
        private static readonly Efl.EvasCb KeyCb = OnKey;

        private static string _dir, _logPath, _fifoPath, _pidPath, _jobPath;
        private static string _eventUrl, _eventToken;
        private static IntPtr _win, _evas, _fifoHandler, _animator, _timeoutTimer;
        private static int _fifoFd = -1, _screenW = DefaultScreenW, _screenH = DefaultScreenH;
        private static bool _busy, _fadingOut;
        private static Job _job;

        // The current popup. Text objects are referenceable so the focus ring and the
        // raise order can find them; everything is in _objs for the fade and teardown.
        private static readonly List<IntPtr> _objs = new List<IntPtr>();
        private static IntPtr _tbTitle, _img, _card, _ring;
        private static readonly List<IntPtr> _pillBg = new List<IntPtr>();
        private static readonly List<IntPtr> _pillText = new List<IntPtr>();
        private static readonly List<int> _pillX = new List<int>(), _pillY = new List<int>(), _pillW = new List<int>(), _pillLabW = new List<int>();
        private static int _pillH, _pilLGap, _sel;

        private static readonly byte[] ReadBuf = new byte[256];
        private static long _logBytes;
        private static double _frameStart;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        // Remote input. The popup reads the remote's evdev nodes read-only (never grabbing) and
        // discards the later evas copy, exactly as the launcher does — so keys work even if the
        // popup window is not given focus. --keys chooses the source (see SetupInput).
        private const int EAgain = 11;
        private const double RescanSeconds = 3.0;
        // EVIOCGRAB = _IOW('E', 0x90, int): 1 takes the device exclusively, 0 gives it back.
        private static readonly UIntPtr EviocGrab = new UIntPtr(0x40044590);
        private static string _keys = "auto";
        private static bool _grabMode;      // keyMode "grab": take the remote while a popup is up
        private static bool _grabbed;       // whether the nodes are currently held
        private static int _homeCode;
        private static readonly NavigationInput _nav = new NavigationInput();
        private static readonly List<int> _inputFds = new List<int>();
        private static readonly List<EvdevReader> _inputReaders = new List<EvdevReader>();
        private static readonly List<string> _inputPaths = new List<string>();
        private static readonly List<IntPtr> _inputHandlers = new List<IntPtr>();
        private static readonly HashSet<string> _openFailed = new HashSet<string>();
        private static IntPtr _rescanTimer;
        private static readonly byte[] InputBuf = new byte[16 * 32];
        private static readonly List<int> _presses = new List<int>();
        private static bool _listFailed;

        // ------------------------------------------------------------------ model

        private sealed class Act
        {
            public string Label;
            public string Event;
            public JsonElement Context;
            public bool Dismiss;
        }

        private sealed class Job
        {
            public string Message;
            public string Title;
            public string Image;
            public string Id;
            public string Location = "tr";
            public string DismissEvent = "dismiss";
            public int Timeout = 60;
            public double Scale = 1.0;
            public bool EmitDismiss = true;
            public bool Solid;
            public readonly List<Act> Actions = new List<Act>();
        }

        // ------------------------------------------------------------------ logging

        private static void Log(string m)
        {
            try
            {
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + m + "\n";
                int bytes = Encoding.UTF8.GetByteCount(line);
                _logBytes += bytes;
                if (_logBytes > LogLimit) { try { File.Delete(_logPath); } catch (Exception) { } _logBytes = bytes; }
                File.AppendAllText(_logPath, line);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ args

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            return null;
        }

        private static bool Flag(string[] args, string name) { return Array.IndexOf(args, name) >= 0; }

        private static string ResolveDir(string[] args)
        {
            string d = Arg(args, "--dir") ?? Environment.GetEnvironmentVariable("POPUP_DIR") ?? "/home/owner";
            return Directory.Exists(d) ? d : Path.GetTempPath();
        }

        private static int Main(string[] args)
        {
            _dir = ResolveDir(args);
            _logPath = Path.Combine(_dir, "notifier.log");
            _fifoPath = Path.Combine(_dir, "notifier.fifo");
            _pidPath = Path.Combine(_dir, "notifier.pid");
            _jobPath = Path.Combine(_dir, "job.json");
            _eventUrl = Arg(args, "--event-url");
            _eventToken = Arg(args, "--event-token");

            if (Flag(args, "--self-test")) { Console.WriteLine("{\"ok\":true}"); return 0; }
            if (Flag(args, "--status")) return Status();

            if (!AcquireInstance()) return 0;
            AppDomain.CurrentDomain.ProcessExit += (s, e) => CleanupFiles();

            try
            {
                SetupEnvironment();
                Log("start pid=" + Libc.getpid() + " uid=" + Libc.getuid());
                Efl.elm_init(0, IntPtr.Zero);
                _win = Efl.elm_win_add(IntPtr.Zero, "popup-notify", Efl.ElmWinNotification);
                if (_win == IntPtr.Zero) { Log("elm_win_add failed"); return 2; }
                _evas = Efl.evas_object_evas_get(_win);
                if (_evas == IntPtr.Zero) { Log("evas_object_evas_get returned null"); return 3; }

                int sx, sy, sw, sh;
                Efl.elm_win_screen_size_get(_win, out sx, out sy, out sw, out sh);
                if (sw > 0 && sh > 0) { _screenW = sw; _screenH = sh; }

                Efl.elm_win_title_set(_win, "popup-notify");
                Efl.elm_win_alpha_set(_win, true);
                Efl.elm_win_borderless_set(_win, true);
                Efl.elm_win_prop_focus_skip_set(_win, false);
                int level = Efl.efl_util_set_notification_window_level(_win, NotificationLevel);
                Efl.elm_win_layer_set(_win, WindowLayer);
                // The compositor may otherwise ignore our geometry for a notification window.
                Efl.elm_win_aux_hint_add(_win, "wm.policy.win.user.geometry", "1");
                Efl.elm_win_aux_hint_add(_win, "wm.comp.win.effect.enable", "0");
                Efl.evas_object_event_callback_add(_win, Efl.EvasCallbackKeyDown, KeyCb, IntPtr.Zero);
                Log("screen " + _screenW + "x" + _screenH + " notification-level=" + level + " layer=" + WindowLayer);

                SetupFifo();
                SetupInput(args);
                Efl.elm_run();
                Efl.elm_shutdown();
            }
            catch (Exception ex) { Log("fatal: " + ex); return 1; }
            return 0;
        }

        // --status: is a resident process ours? Mirrors the launcher's pid probe.
        private static int Status()
        {
            bool running = false;
            try
            {
                int pid;
                if (int.TryParse(File.ReadAllText(_pidPath).Trim(), out pid) && pid > 1 && Libc.kill(pid, 0) == 0)
                {
                    string cmd = File.ReadAllText("/proc/" + pid + "/cmdline").Replace('\0', ' ');
                    running = cmd.Contains("notify.dll");
                }
            }
            catch (Exception) { }
            Console.WriteLine("{\"running\":" + (running ? "true" : "false") + "}");
            return 0;
        }

        private static bool AcquireInstance()
        {
            try
            {
                int other;
                string text = File.Exists(_pidPath) ? File.ReadAllText(_pidPath).Trim() : "";
                if (int.TryParse(text, out other) && other != Libc.getpid() && Libc.kill(other, 0) == 0)
                {
                    string cmd = File.ReadAllText("/proc/" + other + "/cmdline").Replace('\0', ' ');
                    if (cmd.Contains("notify.dll")) return false;   // a popup is already resident
                }
                File.WriteAllText(_pidPath, Libc.getpid().ToString());
            }
            catch (Exception ex) { Log("pidfile: " + ex.Message); }
            return true;
        }

        private static void CleanupFiles()
        {
            try
            {
                int pid;
                if (File.Exists(_pidPath) && int.TryParse(File.ReadAllText(_pidPath).Trim(), out pid) && pid == Libc.getpid())
                {
                    File.Delete(_pidPath);
                    Libc.unlink(_fifoPath);
                }
            }
            catch (Exception) { }
        }

        /// <summary>EFL environment, set through libc because .NET's own env is not what native getenv reads.</summary>
        private static void SetupEnvironment()
        {
            bool egl = Directory.Exists("/dev/dri");
            Libc.setenv("ELM_ENGINE", "gl", 1);
            Libc.setenv("ECORE_EVAS_ENGINE", egl ? "wayland_egl" : "wayland_shm", 1);
            string uid = Libc.getuid().ToString();
            string display = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "wayland-0";
            string runtime = null;
            foreach (string c in new[] { Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), "/run/user/" + uid, "/run", "/run/user/5001" })
                if (!string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, display))) { runtime = c; break; }
            if (runtime == null) runtime = Directory.Exists("/run/user/" + uid) ? "/run/user/" + uid : "/run";
            Libc.setenv("XDG_RUNTIME_DIR", runtime, 1);
            Libc.setenv("WAYLAND_DISPLAY", "wayland-0", 0);
            Libc.setenv("ELM_PROFILE", "tv", 0);
            Libc.setenv("ELM_DISPLAY", "wl", 0);
            Libc.setenv("EVAS_FONT_DPI", "72", 0);
            if (egl)
            {
                Libc.setenv("ELM_ACCEL", "gl", 0);
                Libc.setenv("EVAS_GL_NO_BLACKLIST", "1", 0);
                Libc.setenv("EVAS_GL_PARTIAL_DISABLE", "1", 0);
            }
            Log("env: egl=" + egl + " xdg=" + runtime + " display=" + display + " dri=" + Directory.Exists("/dev/dri"));
        }

        // ------------------------------------------------------------------ fifo

        private static void SetupFifo()
        {
            Libc.unlink(_fifoPath);
            if (Libc.mkfifo(_fifoPath, 438u) != 0) { Log("mkfifo failed"); return; }
            Libc.chmod(_fifoPath, 438u);   // 0666: the Shard writes here as its own uid
            _fifoFd = Libc.openMode(_fifoPath, Libc.O_RDWR | Libc.O_NONBLOCK, 0);
            if (_fifoFd < 0) { Log("open fifo failed"); return; }
            _fifoHandler = Efl.ecore_main_fd_handler_add(_fifoFd, Efl.EcoreFdRead, FifoCb, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        private static byte OnFifo(IntPtr data, IntPtr handler)
        {
            try
            {
                for (int round = 0; round < 8; round++)
                {
                    long n = (long)Libc.read(_fifoFd, ReadBuf, (UIntPtr)ReadBuf.Length);
                    if (n <= 0) break;
                    string words = Encoding.ASCII.GetString(ReadBuf, 0, (int)n);
                    foreach (string raw in words.Split('\n'))
                    {
                        string word = raw.Trim();
                        if (word.Length == 0) continue;
                        Log("command " + word);
                        if (word == "notify") ShowFromDisk();
                        else if (word == "hide") HidePopup(true);
                        else if (word == "show") { if (_job != null && !_busy) BuildAndShow(_job); }
                        else if (word == "toggle") { if (_busy) HidePopup(true); else ShowFromDisk(); }
                    }
                }
            }
            catch (Exception ex) { Log("fifo: " + ex.Message); }
            return 1;
        }

        // ------------------------------------------------------------------ job

        private static void ShowFromDisk()
        {
            Job job;
            try { job = ReadJob(File.ReadAllText(_jobPath)); }
            catch (Exception ex) { Log("job: " + ex.Message); return; }
            if (job == null || job.Message == null) { Log("job: no usable message"); return; }
            BuildAndShow(job);
        }

        private static Job ReadJob(string json)
        {
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                JsonElement r = doc.RootElement;
                Job j = new Job();
                j.Message = Str(r, "message");
                j.Title = Str(r, "title");
                j.Image = Str(r, "image");
                j.Id = Str(r, "id");
                j.Location = Str(r, "location") ?? "tr";
                j.DismissEvent = Str(r, "dismissEvent") ?? "dismiss";
                j.Timeout = Int(r, "timeout", 60);
                j.Scale = Dbl(r, "scale", 1.0);
                j.EmitDismiss = Bool(r, "emitDismiss", true);
                j.Solid = Bool(r, "solid", false);
                string eventUrl = Str(r, "eventUrl");
                if (SafeUrl(eventUrl) != null) { _eventUrl = eventUrl; _eventToken = Str(r, "eventToken"); }
                JsonElement actions;
                if (r.TryGetProperty("actions", out actions) && actions.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement a in actions.EnumerateArray())
                    {
                        Act act = new Act();
                        act.Label = Str(a, "label");
                        act.Event = Str(a, "event");
                        act.Dismiss = Bool(a, "dismiss", false);
                        JsonElement ctx;
                        act.Context = a.TryGetProperty("context", out ctx) ? ctx.Clone() : default(JsonElement);
                        if (act.Label != null && act.Event != null) j.Actions.Add(act);
                    }
                }
                return j;
            }
        }

        private static string Str(JsonElement e, string name)
        {
            JsonElement v;
            return e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        private static int Int(JsonElement e, string name, int dflt)
        {
            JsonElement v; int n;
            return e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out n) ? n : dflt;
        }
        private static double Dbl(JsonElement e, string name, double dflt)
        {
            JsonElement v; double n;
            return e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out n) ? n : dflt;
        }
        private static bool Bool(JsonElement e, string name, bool dflt)
        {
            JsonElement v;
            if (!e.TryGetProperty(name, out v)) return dflt;
            return v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : dflt;
        }
        private static string SafeUrl(string u)
        {
            return !string.IsNullOrEmpty(u) && (u.StartsWith("http://") || u.StartsWith("https://")) ? u : null;
        }

        // ------------------------------------------------------------------ show / hide

        private static void BuildAndShow(Job job)
        {
            try
            {
                TearDown();
                _job = job;
                _sel = 0;
                Log("show: begin title=" + (job.Title ?? "-") + " actions=" + job.Actions.Count + " scale=" + job.Scale + " loc=" + job.Location + " timeout=" + job.Timeout + " image=" + (job.Image ?? "-"));
                if (job.Solid) { BuildSolid(job); return; }

                double s = job.Scale <= 0 ? 1.0 : job.Scale;
                int pad = Sc(BasePad, s), radius = Sc(BaseRadius, s), border = Math.Max(1, Sc(BaseBorder, s));
                int gap = Sc(BaseGap, s), sp = Sc(ShadowPad, s);
                int titleSize = Sc(BaseTitleSize, s), msgSize = Sc(BaseMsgSize, s);
                _pillH = Sc(BasePillH, s); _pilLGap = Sc(BasePillGap, s);
                int pillPadX = Sc(BasePillPadX, s), pillFont = Sc(BasePillFont, s);
                int cardW = Sc(BaseCardW, s);
                int textW = cardW - 2 * pad;

                // The message is wrapped here and drawn as one text object per line, using only
                // the primitives the launcher already uses. Deliberately no Evas textblock: the
                // launcher never calls it, and a missing symbol there would throw and tear the
                // popup down (the focus blip). Keeps the native surface to proven calls.
                List<string> lines = WrapText(job.Message, textW, msgSize);
                var msgLines = new List<IntPtr>();
                int lineH = 0;
                for (int i = 0; i < lines.Count; i++)
                {
                    IntPtr t = Efl.evas_object_text_add(_evas);
                    if (t == IntPtr.Zero) { Log("show: text object unavailable"); TearDown(); return; }
                    Efl.evas_object_text_font_set(t, Font, msgSize);
                    Efl.evas_object_text_text_set(t, lines[i]);
                    Efl.evas_object_text_style_set(t, Efl.EvasTextStyleSoftShadow);
                    Efl.evas_object_text_shadow_color_set(t, 0, 0, 0, 120);
                    if (lineH == 0) lineH = Efl.evas_object_text_vert_advance_get(t);
                    Efl.evas_object_show(t);
                    msgLines.Add(t);
                    _objs.Add(t);
                }
                int msgH = lines.Count * lineH;
                Log("show: message " + lines.Count + " line(s), " + textW + " wide, " + msgH + " tall (font " + msgSize + ")");

                int titleH = 0;
                if (!string.IsNullOrEmpty(job.Title))
                {
                    _tbTitle = Efl.evas_object_text_add(_evas);
                    Efl.evas_object_text_font_set(_tbTitle, FontBold, titleSize);
                    Efl.evas_object_text_text_set(_tbTitle, job.Title);
                    Efl.evas_object_text_style_set(_tbTitle, Efl.EvasTextStyleSoftShadow);
                    Efl.evas_object_text_shadow_color_set(_tbTitle, 0, 0, 0, 120);
                    titleH = Efl.evas_object_text_vert_advance_get(_tbTitle);
                    Efl.evas_object_show(_tbTitle);
                    _objs.Add(_tbTitle);
                }

                // The snapshot, if any: fit the content width, keep the aspect, cap the height.
                int imgW = 0, imgH = 0;
                if (!string.IsNullOrEmpty(job.Image) && File.Exists(job.Image))
                {
                    IntPtr cand = Efl.evas_object_image_add(_evas);
                    Efl.evas_object_image_file_set(cand, job.Image, null);
                    if (Efl.evas_object_image_load_error_get(cand) == Efl.EvasLoadErrorNone)
                    {
                        int nw, nh;
                        Efl.evas_object_image_size_get(cand, out nw, out nh);
                        if (nw > 0 && nh > 0)
                        {
                            imgW = textW;
                            imgH = (int)Math.Round((double)nh / nw * textW);
                            int cap = Sc(BaseImageH, s);
                            if (imgH > cap) imgH = cap;
                            _img = cand;
                            Efl.evas_object_image_filled_set(_img, true);
                            Efl.evas_object_image_smooth_scale_set(_img, true);
                            Efl.evas_object_image_scale_hint_set(_img, Efl.EvasImageScaleHintDynamic);
                            Efl.evas_object_show(_img);
                            _objs.Add(_img);
                        }
                    }
                    if (_img == IntPtr.Zero) Efl.evas_object_del(cand);
                }

                // Measure the action labels so the pills and their rows can be laid out.
                for (int i = 0; i < job.Actions.Count; i++)
                {
                    IntPtr t = Efl.evas_object_text_add(_evas);
                    Efl.evas_object_text_font_set(t, Font, pillFont);
                    Efl.evas_object_text_text_set(t, job.Actions[i].Label);
                    Efl.evas_object_text_style_set(t, Efl.EvasTextStyleSoftShadow);
                    Efl.evas_object_text_shadow_color_set(t, 0, 0, 0, 120);
                    int labW = Efl.evas_object_text_horiz_advance_get(t);
                    Efl.evas_object_show(t);
                    _pillText.Add(t);
                    _pillLabW.Add(labW);
                    _pillW.Add(labW + 2 * pillPadX);
                    _objs.Add(t);
                }
                int rows = PillRows(_pillW, textW, _pilLGap);
                int pillsH = job.Actions.Count == 0 ? 0 : rows * _pillH + (rows - 1) * _pilLGap;

                // Card content height.
                int contentH = pad;
                if (imgH > 0) contentH += imgH + gap;
                if (titleH > 0) contentH += titleH + (int)(gap * 0.4);
                contentH += msgH;
                if (pillsH > 0) contentH += gap + pillsH;
                contentH += pad;

                int winW = cardW + 2 * sp, winH = contentH + 2 * sp;
                int winX, winY;
                Position(winW, winH, job.Location, out winX, out winY);

                // Card, pills and ring: generated ARGB, no asset files.
                uint[] cardPx = new uint[winW * winH];
                RasterCard(winW, winH, sp, radius, border, cardPx);
                _card = MakeImage(winW, winH, cardPx);
                if (_card == IntPtr.Zero) { Log("show: card image failed"); TearDown(); return; }
                _objs.Insert(0, _card);
                Log("show: card " + cardW + "x" + contentH + " (window " + winW + "x" + winH + " at " + winX + "," + winY + "), screen " + _screenW + "x" + _screenH);

                // Content positions inside the window.
                int cx = sp + pad, cy = sp + pad;
                if (imgH > 0) { Efl.evas_object_move(_img, cx, cy); Efl.evas_object_resize(_img, imgW, imgH); cy += imgH + gap; }
                if (titleH > 0) { Efl.evas_object_move(_tbTitle, cx, cy); cy += titleH + (int)(gap * 0.4); }
                for (int i = 0; i < msgLines.Count; i++) Efl.evas_object_move(msgLines[i], cx, cy + i * lineH);
                cy += msgH + (pillsH > 0 ? gap : 0);

                // Lay the pills out row by row, left to right.
                int rowX = cx, rowY = cy;
                for (int i = 0; i < job.Actions.Count; i++)
                {
                    if (rowX != cx && rowX + _pillW[i] > cx + textW) { rowX = cx; rowY += _pillH + _pilLGap; }
                    _pillX.Add(rowX); _pillY.Add(rowY); rowX += _pillW[i] + _pilLGap;
                }
                for (int i = 0; i < job.Actions.Count; i++)
                {
                    uint[] bgPx = new uint[_pillW[i] * _pillH];
                    RasterFill(_pillW[i], _pillH, _pillH / 2, PillAlpha, bgPx);
                    IntPtr bg = MakeImage(_pillW[i], _pillH, bgPx);
                    if (bg == IntPtr.Zero) { Log("show: pill " + i + " image failed"); continue; }
                    Efl.evas_object_move(bg, _pillX[i], _pillY[i]);
                    _pillBg.Add(bg);
                    _objs.Add(bg);
                    Efl.evas_object_move(_pillText[i], _pillX[i] + (_pillW[i] - _pillLabW[i]) / 2, _pillY[i] + (_pillH - Efl.evas_object_text_vert_advance_get(_pillText[i])) / 2);
                }

                // Explicit stacking: card at the bottom, then image, pills, ring, and the text on top.
                Efl.evas_object_lower(_card);
                if (_img != IntPtr.Zero) Efl.evas_object_raise(_img);
                foreach (IntPtr bg in _pillBg) Efl.evas_object_raise(bg);
                if (_tbTitle != IntPtr.Zero) Efl.evas_object_raise(_tbTitle);
                foreach (IntPtr t in msgLines) Efl.evas_object_raise(t);
                foreach (IntPtr t in _pillText) Efl.evas_object_raise(t);

                MoveRing();
                if (job.Actions.Count == 0) _sel = -1;

                Efl.evas_object_move(_win, winX, winY);
                Efl.evas_object_resize(_win, winW, winH);
                // Shown at full opacity at once. No entrance fade: a window whose content is
                // still at alpha 0 is invisible and can lose focus, which is worse than a
                // popup that simply appears.
                ApplyAlpha(255);
                Efl.evas_object_show(_win);
                Efl.elm_win_activate(_win);
                _busy = true;
                _fadingOut = false;
                SetGrab(true);

                if (job.Timeout > 0) { CancelTimer(); _timeoutTimer = Efl.ecore_timer_add(job.Timeout, TimeoutCb, IntPtr.Zero); }
                Log("shown: " + job.Actions.Count + " actions, " + rows + " row(s), image=" + (_img != IntPtr.Zero) + " timeout=" + job.Timeout);
            }
            catch (Exception ex) { Log("show: " + ex); TearDown(); }
        }

        /// <summary>
        /// A diagnostic the page can trigger: paint the whole screen an opaque colour. If this
        /// appears, the notification window composites over the app and any blank popup is a
        /// drawing problem; if it does not, the window is not being shown on top at all.
        /// </summary>
        private static void BuildSolid(Job job)
        {
            try
            {
                TearDown();
                _job = job;
                IntPtr rect = Efl.evas_object_rectangle_add(_evas);
                Efl.evas_object_color_set(rect, 220, 40, 200, 255);
                Efl.evas_object_move(rect, 0, 0);
                Efl.evas_object_resize(rect, _screenW, _screenH);
                Efl.evas_object_show(rect);
                _objs.Add(rect);
                IntPtr t = Efl.evas_object_text_add(_evas);
                Efl.evas_object_text_font_set(t, FontBold, 72);
                Efl.evas_object_text_text_set(t, "SOLID OVERLAY TEST");
                Efl.evas_object_move(t, 120, 120);
                Efl.evas_object_show(t);
                _objs.Add(t);
                Efl.evas_object_move(_win, 0, 0);
                Efl.evas_object_resize(_win, _screenW, _screenH);
                Efl.evas_object_show(_win);
                Efl.elm_win_activate(_win);
                _busy = true; _fadingOut = false;
                SetGrab(true);
                CancelTimer();
                _timeoutTimer = Efl.ecore_timer_add(job.Timeout > 0 ? job.Timeout : 6, TimeoutCb, IntPtr.Zero);
                Log("show: solid overlay " + _screenW + "x" + _screenH + " timeout=" + (job.Timeout > 0 ? job.Timeout : 6));
            }
            catch (Exception ex) { Log("show solid: " + ex); TearDown(); }
        }

        private static void HidePopup(bool emitDismiss)
        {
            if (!_busy || _fadingOut) return;
            if (emitDismiss && _job != null && _job.EmitDismiss) SendActionEvent(_job.DismissEvent, DismissLabel(), null);
            CancelTimer();
            TearDown();
        }

        private static string DismissLabel()
        {
            if (_job != null) foreach (Act a in _job.Actions) if (a.Event == _job.DismissEvent) return a.Label;
            return "Dismiss";
        }

        private static byte OnFadeOut(IntPtr data)
        {
            try
            {
                double p = Progress();
                if (p >= 1.0) { TearDown(); return 0; }
                ApplyAlpha((int)(255 * (1.0 - p)));
                return 1;
            }
            catch (Exception ex) { Log("fadeout: " + ex); TearDown(); return 0; }
        }

        private static void TearDown()
        {
            SetGrab(false);
            CancelTimer();
            if (_animator != IntPtr.Zero) { Efl.ecore_animator_del(_animator); _animator = IntPtr.Zero; }
            foreach (IntPtr o in _objs) { try { Efl.evas_object_del(o); } catch (Exception) { } }
            _objs.Clear(); _pillBg.Clear(); _pillText.Clear();
            _pillX.Clear(); _pillY.Clear(); _pillW.Clear(); _pillLabW.Clear();
            _tbTitle = _img = _card = _ring = IntPtr.Zero;
            _busy = false; _fadingOut = false;
            try { Efl.evas_object_hide(_win); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ keys

        private static void OnKey(IntPtr data, IntPtr evas, IntPtr obj, IntPtr eventInfo)
        {
            try
            {
                if (_keys == "evdev") return;                          // evdev only: ignore what evas delivers
                string key = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(eventInfo));   // Evas_Event_Key_Down.keyname
                if (_nav.IsEcho(key, Efl.ecore_time_get())) return;    // the raw evdev press was already handled
                HandleKey(key);
            }
            catch (Exception ex) { Log("key: " + ex); }
        }

        /// <summary>A navigation key, whether it arrived through evas or was read from the remote.</summary>
        private static void HandleKey(string key)
        {
            try
            {
                if (!_busy || _fadingOut || _job == null) return;
                if (key == "Left") { Move(-1); return; }
                if (key == "Right") { Move(1); return; }
                if (key == "Return" || key == "KP_Enter") { if (_sel >= 0) Activate(_sel); return; }
                if (key == "Escape" || key == "XF86Back" || key == "BackSpace" || key == "XF86Home") { HidePopup(true); return; }
                if (key != null && key.Length == 1 && key[0] >= '1' && key[0] <= '9')
                {
                    int i = key[0] - '1';
                    if (i < _job.Actions.Count) Activate(i);
                }
            }
            catch (Exception ex) { Log("key: " + ex); }
        }

        private static void Move(int delta)
        {
            if (_job == null || _job.Actions.Count == 0) return;
            int n = _job.Actions.Count;
            _sel = ((_sel + delta) % n + n) % n;
            MoveRing();
        }

        /// <summary>The focus ring is regenerated for each move: pills differ in width, and a scaled ring would blur its corners.</summary>
        private static void MoveRing()
        {
            if (_ring != IntPtr.Zero) { try { Efl.evas_object_del(_ring); _objs.Remove(_ring); } catch (Exception) { } _ring = IntPtr.Zero; }
            if (_sel < 0 || _sel >= _pillBg.Count) return;
            int w = _pillW[_sel] + 8, h = _pillH + 8, width = 3;
            uint[] px = new uint[w * h];
            RasterRing(w, h, h / 2, width, px);
            _ring = MakeImage(w, h, px);
            if (_ring == IntPtr.Zero) return;
            Efl.evas_object_move(_ring, _pillX[_sel] - 4, _pillY[_sel] - 4);
            Efl.evas_object_raise(_ring);
            _objs.Add(_ring);
        }

        private static void Activate(int i)
        {
            if (_job == null || i < 0 || i >= _job.Actions.Count) return;
            Act act = _job.Actions[i];
            SendActionEvent(act.Event, act.Label, act.Context);
            HidePopup(false);   // the action event was already sent for this action
        }

        private static void SendActionEvent(string name, string label, JsonElement? context)
        {
            if (string.IsNullOrEmpty(name)) return;
            var payload = new Dictionary<string, object> { { "event", name }, { "id", _job != null ? _job.Id : null }, { "label", label } };
            if (context.HasValue && context.Value.ValueKind == JsonValueKind.Object) payload["context"] = context.Value;
            string json = JsonSerializer.Serialize(payload);
            string url = _eventUrl, token = _eventToken;
            if (SafeUrl(url) == null) { Log("event " + name + " (no event url)"); return; }
            Task.Run(async () =>
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    if (!string.IsNullOrEmpty(token)) req.Headers.Add("X-Popup-Token", token);
                    var res = await Http.SendAsync(req).ConfigureAwait(false);
                    if ((int)res.StatusCode >= 300) Log("event " + name + " -> HTTP " + (int)res.StatusCode);
                }
                catch (Exception ex) { Log("event " + name + " failed: " + ex.Message); }
            });
        }

        // ------------------------------------------------------------------ remote input (read-only evdev)

        /// <summary>
        /// Opens the remote's input nodes read-only and watches them through ecore, exactly as the
        /// launcher does. Reading does not consume a key, so the system still sees it, and the later
        /// evas copy is dropped by NavigationInput.IsEcho. This is what makes the popup work even
        /// when the window is not given focus.
        ///   --keys auto   (default) evdev when a node opens, evas otherwise
        ///   --keys evas   never open a node: the focused-window path only
        ///   --keys evdev  watch nodes and ignore evas key events
        ///   --home-code N also dismiss when raw code N is pressed (0 = off)
        /// </summary>
        private static void SetupInput(string[] args)
        {
            string mode = Arg(args, "--keys");
            if (mode == "grab") { _grabMode = true; _keys = "auto"; }
            else if (mode == "evas" || mode == "evdev" || mode == "auto") _keys = mode;
            int home;
            if (int.TryParse(Arg(args, "--home-code") ?? "", out home) && home > 0 && home < 768) _homeCode = home;
            if (_keys == "evas") { Log("keys: evas only"); return; }
            WatchNewNodes();
            Log("keys: " + _keys + (_grabMode ? " (exclusive grab while a popup is up)" : "") +
                " (evdev watched" + (_homeCode > 0 ? ", home code " + _homeCode : "") + ")");
            _rescanTimer = Efl.ecore_timer_add(RescanSeconds, RescanCb, IntPtr.Zero);
        }

        /// <summary>
        /// Takes the remote exclusively while a popup is up (EVIOCGRAB), so the keys the popup uses
        /// are not also delivered to the app behind it, and gives it back on hide. A grab that
        /// fails (another process holding the device) is logged and left alone; the fd closing
        /// when the process exits releases it in any case, so a crash cannot lock the remote.
        /// </summary>
        private static void SetGrab(bool on)
        {
            if (!_grabMode || _grabbed == on) return;
            int n = 0;
            for (int i = 0; i < _inputFds.Count; i++)
            {
                if (_inputFds[i] < 0) continue;
                if (Libc.ioctl(_inputFds[i], EviocGrab, on ? (UIntPtr)1 : (UIntPtr)0) == 0) n++;
                else Log("input: grab " + (on ? "on" : "off") + " failed for " + _inputPaths[i]);
            }
            _grabbed = on;
            Log("input: " + (on ? "grabbed " : "released ") + n + " node(s)");
        }

        /// <summary>Opens every remote node not being watched yet. Returns how many were added.</summary>
        private static int WatchNewNodes()
        {
            List<string> nodes;
            try { nodes = InputDevices.RemoteNodes(File.ReadAllText("/proc/bus/input/devices")); }
            catch (Exception ex) { if (!_listFailed) Log("input: cannot list devices: " + ex.Message); _listFailed = true; return 0; }
            _listFailed = false;
            int added = 0;
            foreach (string node in nodes)
            {
                if (_inputPaths.Contains(node)) continue;
                int fd = Libc.open(node, Libc.O_RDONLY | Libc.O_NONBLOCK);
                if (fd < 0)
                {
                    if (_openFailed.Add(node)) Log("input: cannot open " + node);
                    continue;
                }
                _openFailed.Remove(node);
                int index = _inputFds.IndexOf(-1);   // a node that went away leaves a free slot
                bool reuse = index >= 0;
                if (!reuse) index = _inputFds.Count;
                IntPtr handler = Efl.ecore_main_fd_handler_add(fd, Efl.EcoreFdRead, InputCb, (IntPtr)index, IntPtr.Zero, IntPtr.Zero);
                if (handler == IntPtr.Zero) { Libc.close(fd); Log("input: cannot watch " + node); continue; }
                if (reuse) { _inputFds[index] = fd; _inputReaders[index] = new EvdevReader(); _inputPaths[index] = node; _inputHandlers[index] = handler; }
                else { _inputFds.Add(fd); _inputReaders.Add(new EvdevReader()); _inputPaths.Add(node); _inputHandlers.Add(handler); }
                added++;
                if (_grabMode && _grabbed) Libc.ioctl(fd, EviocGrab, (UIntPtr)1);
                Log("input: watching " + node);
            }
            return added;
        }

        private static byte OnRescan(IntPtr data)
        {
            try { WatchNewNodes(); } catch (Exception ex) { Log("input: rescan: " + ex.Message); }
            return 1;
        }

        /// <summary>A node that was unplugged stops being watched; it is opened again when it returns.</summary>
        private static void DropNode(int index)
        {
            if (index < 0 || index >= _inputFds.Count || _inputFds[index] < 0) return;
            Log("input: lost " + _inputPaths[index]);
            if (_inputHandlers[index] != IntPtr.Zero) Efl.ecore_main_fd_handler_del(_inputHandlers[index]);
            Libc.close(_inputFds[index]);
            _inputFds[index] = -1;
            _inputHandlers[index] = IntPtr.Zero;
            _inputPaths[index] = null;
        }

        private static byte OnInput(IntPtr data, IntPtr handler)
        {
            try
            {
                int index = (int)data;
                if (index < 0 || index >= _inputFds.Count || _inputFds[index] < 0) return 1;
                for (int round = 0; round < 8; round++)
                {
                    long n = (long)Libc.read(_inputFds[index], InputBuf, (UIntPtr)InputBuf.Length);
                    if (n < 0 && Marshal.GetLastWin32Error() != EAgain) { DropNode(index); break; }
                    if (n <= 0) break;
                    _presses.Clear();
                    _inputReaders[index].Feed(InputBuf, (int)n, _presses);
                    for (int k = 0; k < _presses.Count; k++)
                    {
                        int code = _presses[k];
                        if (_homeCode > 0 && code == _homeCode) { if (_busy && !_fadingOut) HidePopup(true); continue; }
                        NavFromInput(code);
                    }
                }
            }
            catch (Exception ex) { Log("input: " + ex.Message); }
            return 1;
        }

        private static void NavFromInput(int code)
        {
            string key = _nav.Press(code, _busy && !_fadingOut && _job != null, Efl.ecore_time_get());
            if (key != null) HandleKey(key);
        }

        // ------------------------------------------------------------------ timing / fade

        private static byte OnTimeout(IntPtr data)
        {
            _timeoutTimer = IntPtr.Zero;
            try { HidePopup(true); } catch (Exception ex) { Log("timeout: " + ex); }
            return 0;
        }

        private static void CancelTimer()
        {
            if (_timeoutTimer != IntPtr.Zero) { Efl.ecore_timer_del(_timeoutTimer); _timeoutTimer = IntPtr.Zero; }
        }

        private static void StartAnimator(Efl.TaskCb cb)
        {
            if (_animator != IntPtr.Zero) Efl.ecore_animator_del(_animator);
            _frameStart = Efl.ecore_time_get();
            _animator = Efl.ecore_animator_add(cb, IntPtr.Zero);
        }

        private static double Progress()
        {
            double p = (Efl.ecore_time_get() - _frameStart) / FadeSeconds;
            return p < 0 ? 0 : p > 1 ? 1 : p;
        }

        private static byte OnFrame(IntPtr data)
        {
            try
            {
                double p = Progress();
                if (p >= 1.0) { ApplyAlpha(255); _animator = IntPtr.Zero; return 0; }
                ApplyAlpha((int)(255 * p));
                return 1;
            }
            catch (Exception ex) { Log("frame: " + ex); _animator = IntPtr.Zero; return 0; }
        }

        /// <summary>Tints every popup object white at the given opacity (premultiplied), so the whole popup fades as one.</summary>
        private static void ApplyAlpha(int a)
        {
            for (int i = 0; i < _objs.Count; i++) Efl.evas_object_color_set(_objs[i], a, a, a, a);
        }

        // ------------------------------------------------------------------ layout helpers

        private static int Sc(int baseValue, double scale) { return (int)Math.Round(baseValue * scale); }

        private static int PillRows(List<int> widths, int maxW, int gap)
        {
            if (widths.Count == 0) return 0;
            int rows = 1, x = 0;
            for (int i = 0; i < widths.Count; i++)
            {
                if (x != 0 && x + widths[i] > maxW) { rows++; x = 0; }
                x += widths[i] + gap;
            }
            return rows;
        }

        private static void Position(int winW, int winH, string location, out int x, out int y)
        {
            int m = EdgeMargin;
            switch (location)
            {
                case "tl": x = m; y = m; break;
                case "bl": x = m; y = _screenH - winH - m; break;
                case "br": x = _screenW - winW - m; y = _screenH - winH - m; break;
                case "center": x = (_screenW - winW) / 2; y = (_screenH - winH) / 2; break;
                default: x = _screenW - winW - m; y = m; break;   // tr
            }
            if (x < 0) x = 0;
            if (y < 0) y = 0;
        }

        /// <summary>Greedy word wrap, measured with the same font the message is drawn in.</summary>
        private static List<string> WrapText(string text, int maxW, int size)
        {
            var lines = new List<string>();
            IntPtr m = Efl.evas_object_text_add(_evas);
            if (m == IntPtr.Zero) { lines.Add(text); return lines; }
            Efl.evas_object_text_font_set(m, Font, size);
            string[] paras = text.Replace("\r", "").Split('\n');
            for (int p = 0; p < paras.Length; p++)
            {
                string cur = "";
                string[] words = paras[p].Split(' ');
                for (int w = 0; w < words.Length; w++)
                {
                    if (words[w].Length == 0) continue;
                    string cand = cur.Length == 0 ? words[w] : cur + " " + words[w];
                    Efl.evas_object_text_text_set(m, cand);
                    bool fits = Efl.evas_object_text_horiz_advance_get(m) <= maxW;
                    if (fits || cur.Length == 0) cur = cand;
                    else { lines.Add(cur); cur = words[w]; }
                }
                lines.Add(cur);
            }
            Efl.evas_object_del(m);
            return lines;
        }

        // ------------------------------------------------------------------ image raster

        /// <summary>An Evas image of w x h filled from raw premultiplied ARGB pixels.</summary>
        private static IntPtr MakeImage(int w, int h, uint[] px)
        {
            IntPtr img = Efl.evas_object_image_add(_evas);
            Efl.evas_object_image_alpha_set(img, true);
            Efl.evas_object_image_size_set(img, w, h);      // the pixel buffer's size
            Efl.evas_object_image_filled_set(img, true);
            IntPtr dst = Efl.evas_object_image_data_get(img, true);
            if (dst == IntPtr.Zero) { Log("image: no writable buffer for " + w + "x" + h); Efl.evas_object_del(img); return IntPtr.Zero; }
            WritePixels(dst, Efl.evas_object_image_stride_get(img), px, w, h);
            Efl.evas_object_image_data_set(img, dst);
            Efl.evas_object_image_data_update_add(img, 0, 0, w, h);
            // The object's own geometry: without a resize the object is 0x0 and draws nothing,
            // however big its pixel buffer is. This is separate from image_size_set above.
            Efl.evas_object_resize(img, w, h);
            Efl.evas_object_show(img);
            return img;
        }

        private static void WritePixels(IntPtr data, int stride, uint[] px, int w, int h)
        {
            var row = new int[w];
            if (stride < w * 4) stride = w * 4;
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(px, y * w * 4, row, 0, w * 4);
                Marshal.Copy(row, 0, IntPtr.Add(data, y * stride), w);
            }
        }

        /// <summary>Premultiplied ARGB word for an 0xRRGGBB colour at 0..1 alpha and 0..1 coverage.</summary>
        private static uint Argb(int rgb, float alpha, float cover)
        {
            float a = alpha * cover;
            if (a <= 0) return 0;
            if (a > 1) a = 1;
            int ai = (int)(a * 255 + 0.5f);
            int r = (int)((rgb >> 16 & 0xFF) * a + 0.5f);
            int g = (int)((rgb >> 8 & 0xFF) * a + 0.5f);
            int b = (int)((rgb & 0xFF) * a + 0.5f);
            return (uint)(ai << 24 | r << 16 | g << 8 | b);
        }

        private static void Over(ref uint dst, uint src)
        {
            float sa = (src >> 24) / 255f, da = (dst >> 24) / 255f;
            float oa = sa + da * (1 - sa);
            if (oa <= 0) { dst = 0; return; }
            int r = (int)(((src >> 16 & 0xFF) + (dst >> 16 & 0xFF) * da * (1 - sa)) / oa + 0.5f);
            int g = (int)(((src >> 8 & 0xFF) + (dst >> 8 & 0xFF) * da * (1 - sa)) / oa + 0.5f);
            int b = (int)(((src & 0xFF) + (dst & 0xFF) * da * (1 - sa)) / oa + 0.5f);
            dst = (uint)((int)(oa * 255 + 0.5f) << 24 | (r & 0xFF) << 16 | (g & 0xFF) << 8 | (b & 0xFF));
        }

        /// <summary>The card: rounded fill, a thin light border, and a short shadow ramp outside it.</summary>
        private static void RasterCard(int w, int h, int sp, int radius, int border, uint[] px)
        {
            float halfW = (w - 2 * sp) / 2f, halfH = (h - 2 * sp) / 2f;
            float ccx = sp + halfW, ccy = sp + halfH;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = Sdf(x + 0.5f, y + 0.5f, ccx, ccy, halfW, halfH, radius);
                    uint p;
                    if (d > 0)
                    {
                        float t = 1 - d / Math.Max(1, sp);
                        p = Argb(0x000000, ShadowAlpha, t > 0 ? t * t : 0);
                    }
                    else
                    {
                        float fillCover = Clamp(0.5f - d, 0, 1);
                        float innerCover = Clamp(0.5f - (d + border), 0, 1);
                        p = Argb(CardRgb, CardAlpha, fillCover);
                        float band = fillCover - innerCover;
                        if (band > 0) Over(ref p, Argb(CardBorderRgb, CardBorderAlpha, band));
                    }
                    px[y * w + x] = p;
                }
        }

        private static void RasterFill(int w, int h, int radius, float alpha, uint[] px)
        {
            float halfW = w / 2f, halfH = h / 2f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float cover = Clamp(0.5f - Sdf(x + 0.5f, y + 0.5f, halfW, halfH, halfW, halfH, radius), 0, 1);
                    px[y * w + x] = Argb(0xFFFFFF, alpha, cover);
                }
        }

        private static void RasterRing(int w, int h, int radius, int width, uint[] px)
        {
            float halfW = w / 2f, halfH = h / 2f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = Sdf(x + 0.5f, y + 0.5f, halfW, halfH, halfW, halfH, radius);
                    float band = Clamp(0.5f - d, 0, 1) - Clamp(0.5f - (d + width), 0, 1);
                    px[y * w + x] = band > 0 ? Argb(0xFFFFFF, 0.95f, band) : 0;
                }
        }

        /// <summary>Signed distance to a rounded rectangle centred at (cx, cy): negative inside.</summary>
        private static float Sdf(float px, float py, float cx, float cy, float halfW, float halfH, float radius)
        {
            float dx = Math.Abs(px - cx) - (halfW - radius);
            float dy = Math.Abs(py - cy) - (halfH - radius);
            float ax = dx > 0 ? dx : 0, ay = dy > 0 ? dy : 0;
            return (float)Math.Sqrt(ax * ax + ay * ay) - radius;
        }

        private static float Clamp(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }
    }
}
