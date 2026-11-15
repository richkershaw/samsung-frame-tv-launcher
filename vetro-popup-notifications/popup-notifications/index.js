'use strict';
// =============================================================================
// Vetro Shard: popup notifications (the glue half)
// =============================================================================
//
// A Shard cannot draw on the TV: the API has no window, and the launcher's own
// renderer is a native .NET assembly that a plugin may not replace. So this is
// split in two, the way Vetro's launcher and the 8BitDo Guide Shard are split:
//
//   * THIS SHARD (JavaScript, runs inside the Vetro service): a small HTTP
//     listener Home Assistant posts notifications to, the snapshot download, the
//     native process's supervision, and the relay of button presses back to Home
//     Assistant as webhook events.
//
//   * notify.dll (native, ships in this bundle as notify.dll.gz.b64): a
//     resident process the TV's own /usr/bin/dotnet runs as root, drawing an
//     EFL notification window over whatever app is on screen.
//
// The two talk over a FIFO (this -> notifier: "notify"/"hide") and an event POST
// (notifier -> this -> Home Assistant). See docs/PROTOCOL.md, and for the exact
// window trick see the launcher's dock/src/Program.cs in the Vetro source.
//
// Plain ES5 on purpose: older TVs run Node 4.4 (no arrow functions, no let/const,
// no async/await, no template literals, no classes).

var http = require('http');
var https = require('https');
var fs = require('fs');
var path = require('path');

var UNIT = 'popup-notifications';
var DLL = 'notify.dll';
var DLL_B64 = 'notify.dll.gz.b64';

var TICK_MS = 5000;            // notifier supervision
var START_WAIT_MS = 15000;
var BOOT_WAIT_MS = 75000;      // a start in the first minutes after a TV boot is slow
var MAX_BODY = 64 * 1024;      // notification payload ceiling
var MAX_IMAGE = 5 * 1024 * 1024;
var MAX_ACTIONS = 4;           // plus the always-present Dismiss
var MAX_MSG = 400, MAX_TITLE = 60, MAX_LABEL = 24, MAX_ID = 120, MAX_CONTEXT_KEYS = 20;
var LOCATIONS = ['tl', 'tr', 'bl', 'br', 'center'];
var IMG_EXT = { 'image/jpeg': 'jpg', 'image/jpg': 'jpg', 'image/png': 'png', 'image/gif': 'gif', 'image/webp': 'webp', 'image/bmp': 'bmp' };
var SAFE_FILE = /^[A-Za-z0-9._-]{1,64}$/;
// A key name goes on a command line (like Vetro's own remapper): X keysyms only.
var KEY_NAME = /^[A-Za-z0-9_]{1,32}$/;
var REDIRECTS = 3;

// Every value the page can set. Unknown keys are ignored, everything is clamped.
var DEFAULTS = {
    port: 8079,
    bind: '0.0.0.0',
    token: '',                 // shared secret for the listener; '' = open on the LAN
    webhookUrl: '',          // e.g. http://homeassistant.local:8123/api/webhook/abcdef
    emitDismiss: true,         // fire an event when the popup closes
    dismissEvent: 'dismiss',
    dismissLabel: 'Dismiss',
    pauseMedia: false,         // payload default for "pauseMedia"
    resumeMedia: false,        // send pauseKey again when the popup closes
    pauseKey: 'XF86AudioPlay',
    keyMode: 'auto',           // auto | evas | evdev | grab — see the README on remote keys
    homeKeyCode: 0,            // raw evdev code that also dismisses (0 = off)
    defaultTimeout: 60,
    defaultLocation: 'tr'
};

module.exports = function (ctx) {

    // ---------------------------------------------------------------- plumbing

    var timer = null, server = null, active = false, generation = 0, busy = false;
    var pausedForNotification = false;
    var detail = { running: false, listening: false, port: null, lastResult: 'stopped', lastEvent: null, error: null };

    function config() {
        var raw = {};
        try { raw = ctx.config.get() || {}; } catch (e) { raw = {}; }
        var out = {}, k;
        for (k in DEFAULTS) if (DEFAULTS.hasOwnProperty(k)) out[k] = raw[k];
        out.port = clampInt(out.port, 1, 65535, DEFAULTS.port);
        out.bind = typeof out.bind === 'string' && out.bind ? out.bind : DEFAULTS.bind;
        out.token = typeof out.token === 'string' ? out.token : '';
        out.webhookUrl = typeof out.webhookUrl === 'string' ? out.webhookUrl : '';
        out.emitDismiss = out.emitDismiss !== false;
        out.dismissEvent = safeEvent(out.dismissEvent) || DEFAULTS.dismissEvent;
        out.dismissLabel = shortString(out.dismissLabel, MAX_LABEL) || DEFAULTS.dismissLabel;
        out.pauseMedia = out.pauseMedia === true;
        out.resumeMedia = out.resumeMedia === true;
        out.pauseKey = KEY_NAME.test(out.pauseKey || '') ? out.pauseKey : DEFAULTS.pauseKey;
        out.keyMode = ['auto', 'evas', 'evdev', 'grab'].indexOf(out.keyMode) >= 0 ? out.keyMode : DEFAULTS.keyMode;
        out.homeKeyCode = clampInt(out.homeKeyCode, 0, 767, DEFAULTS.homeKeyCode);
        out.defaultTimeout = clampInt(out.defaultTimeout, 0, 3600, DEFAULTS.defaultTimeout);
        out.defaultLocation = LOCATIONS.indexOf(out.defaultLocation) >= 0 ? out.defaultLocation : DEFAULTS.defaultLocation;
        return out;
    }

    function clampInt(v, lo, hi, dflt) {
        var n = parseInt(v, 10);
        if (isNaN(n)) return dflt;
        return Math.max(lo, Math.min(hi, n));
    }

    function shortString(v, max) {
        if (typeof v !== 'string') return null;
        var s = v.replace(/[\u0000-\u001f\u007f]/g, ' ').trim();
        return s ? s.slice(0, max) : null;
    }

    // An event name is echoed back to Home Assistant; keep it to a token.
    function safeEvent(v) {
        var s = shortString(v, 64);
        return s && /^[A-Za-z0-9._:-]+$/.test(s) ? s : null;
    }

    function log(m) {
        var line = stamp() + ' ' + m + '\n';
        try {
            var file = path.join(ctx.dir, 'popup.log');
            try { if (fs.statSync(file).size > 128 * 1024) fs.unlinkSync(file); } catch (e) { /* no file yet */ }
            fs.appendFileSync(file, line);
        } catch (e) { /* logging must never break a request */ }
    }

    function stamp() {
        function p(n) { return n < 10 ? '0' + n : '' + n; }
        var d = new Date();
        return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) + ' ' + p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
    }

    // Shell-rientation: quote, then hand the whole script to `sh` through stdin as
    // base64 so nothing in it (URLs, JSON) has to survive the outer quoting.
    function q(s) { return "'" + String(s).replace(/'/g, "'\\''") + "'"; }
    function sh(script) {
        var b = Buffer.from ? Buffer.from(script) : new Buffer(script);
        return "echo " + q(b.toString('base64')) + " | base64 -d | sh";
    }

    function execute(script, ms) {
        return ctx.execute(sh(script), ms || 10000);
    }

    function fds() {
        var dir = ctx.dir;
        return {
            dir: dir,
            fifo: path.join(dir, 'notifier.fifo'),
            job: path.join(dir, 'job.json'),
            media: path.join(dir, 'media'),
            dll: path.join(dir, DLL)
        };
    }

    // ---- diagnostics ----------------------------------------------------------
    // The notifier runs as root and writes its log into this same folder; the Shard
    // reads it so the page can show it without shell access to the TV.

    function tail(file, n) {
        try {
            var lines = fs.readFileSync(file, 'utf8').replace(/\r/g, '').split('\n');
            if (lines.length && lines[lines.length - 1] === '') lines.pop();
            return lines.slice(-n);
        } catch (e) { return null; }
    }

    function fileInfo(p) {
        try { return { present: true, size: fs.statSync(p).size }; } catch (e) { return { present: false }; }
    }

    // ------------------------------------------------------------------ notifier

    // notify.dll.gz.b64 -> <ctx.dir>/notify.dll, shipped base64'd because a Shard
    // bundle is text-only. The runtimeconfig.json beside it ships as plain text.
    function startCommand(cfg) {
        var f = fds();
        var eventUrl = 'http://127.0.0.1:' + cfg.port + '/event';
        var args = '/usr/bin/dotnet ' + q(f.dll) + ' --dir ' + q(f.dir) +
            ' --event-url ' + q(eventUrl) + ' --event-token ' + q(cfg.token) +
            ' --keys ' + q(cfg.keyMode) + ' --home-code ' + cfg.homeKeyCode;
        // Before a first start (or after an update changed notify.dll) the binary is
        // decoded from the bundle; otherwise the resident process is left alone. The
        // .service is only started when it is not already active. The base64 file is
        // read from disk on the TV (it is already this plugin's own folder), so the
        // payload is never embedded in the command line.
        var dllB64 = path.join(__dirname, DLL_B64);
        var script = "set -e\n" +
            "if [ ! -f " + q(f.dll) + " ] || [ " + q(dllB64) + " -nt " + q(f.dll) + " ]; then\n" +
            "  base64 -d < " + q(dllB64) + " | gzip -d > " + q(f.dll + '.new') + "\n" +
            "  chmod 600 " + q(f.dll + '.new') + "\n" +
            "  mv " + q(f.dll + '.new') + " " + q(f.dll) + "\n" +
            "fi\n" +
            "if ! systemctl is-active --quiet " + UNIT + ".service; then\n" +
            "  systemctl stop " + UNIT + ".service >/dev/null 2>&1 || true\n" +
            "  systemctl reset-failed " + UNIT + ".service >/dev/null 2>&1 || true\n" +
            "  systemd-run --unit=" + UNIT + " --collect -p Restart=on-failure -p RestartSec=2 " +
            "-p StandardOutput=null -p StandardError=null " + args + " >/dev/null 2>&1\n" +
            "fi\n" +
            "/usr/bin/dotnet " + q(f.dll) + " --status --dir " + q(f.dir);
        return execute(script, 15000);
    }

    function stopCommand() {
        return "systemctl stop " + UNIT + ".service >/dev/null 2>&1 || true; systemctl reset-failed " + UNIT + ".service >/dev/null 2>&1 || true";
    }

    // Start arguments (key mode, home code) are read only when the notifier process starts, so a
    // settings change stops it and the supervision tick brings it back within a few seconds with
    // the new arguments. Without this, changing the key mode on the page would do nothing.
    function restartNotifier() {
        generation++;
        detail.lastResult = 'restarting';
        return execute(stopCommand(), 8000).catch(function () { /* the tick will try again */ });
    }

    // Started by start(), re-checked on a timer: if the process died, bring it back.
    function check(cfg) {
        if (!active || busy) return;
        busy = true;
        var mine = generation;
        var primary = Promise.resolve().then(function () { return startCommand(cfg); });
        primary.then(function (result) {
            if (!active) return execute(stopCommand(), 10000);
            if (mine !== generation) return;
            if (result && (result.exit_code || result.timed_out)) {
                throw new Error((result.stderr || result.stdout || 'notifier did not start').toString().trim());
            }
            var status = {};
            try { status = JSON.parse((result.stdout || '').split('\n').pop()); } catch (e) { status = {}; }
            detail.running = true;
            detail.error = null;
            detail.lastResult = status.running === false ? 'starting' : 'running';
        }).catch(function (error) {
            if (active && mine === generation) {
                detail.running = false;
                detail.error = String((error && error.message) || error).slice(0, 200);
                detail.lastResult = 'failed';
            }
        }).then(function () { busy = false; });
    }

    // Pushes one word to the notifier's FIFO (it holds the FIFO open read/write, so
    // the write never blocks). Failure is not fatal: the next notify tries again.
    function poke(word) {
        var f = fds();
        return execute("printf " + q(word + '\n') + " > " + q(f.fifo), 5000).then(function (r) {
            if (r && (r.exit_code || r.timed_out)) throw new Error('fifo write failed: ' + (r.stderr || '').trim());
            return true;
        });
    }

    // ------------------------------------------------------------------ images

    // Downloads an http(s) image into <ctx.dir>/media and calls back with the local
    // path, or with null when the payload gave no image. A local absolute path that
    // already exists on the TV is passed through untouched.
    function resolveImage(src, cb) {
        if (!src) return cb(null);
        if (typeof src === 'string' && src.charAt(0) === '/') {
            return cb(fs.existsSync(src) ? src : null);
        }
        if (typeof src !== 'string' || !/^https?:\/\//i.test(src)) return cb(null);
        fetchImage(src, 0, cb);
    }

    function fetchImage(url, redirects, cb) {
        var mod = /^https:/i.test(url) ? https : http;
        var req;
        try {
            req = mod.get(url, { timeout: 8000 }, function (res) {
                if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location && redirects < REDIRECTS) {
                    res.resume();
                    return fetchImage(absolutise(url, res.headers.location), redirects + 1, cb);
                }
                if (res.statusCode !== 200) { res.resume(); log('image: HTTP ' + res.statusCode + ' for ' + url); return cb(null); }
                var type = (res.headers['content-type'] || '').split(';')[0].trim().toLowerCase();
                var ext = IMG_EXT[type];
                if (!ext) { res.resume(); log('image: unsupported type ' + type); return cb(null); }
                var chunks = [], size = 0, tooBig = false;
                res.on('data', function (d) {
                    size += d.length;
                    if (size > MAX_IMAGE) { tooBig = true; res.destroy(); return; }
                    chunks.push(d);
                });
                res.on('end', function () {
                    if (tooBig) { log('image: over ' + MAX_IMAGE + ' bytes'); return cb(null); }
                    try {
                        var dir = fds().media;
                        if (!fs.existsSync(dir)) fs.mkdirSync(dir, 448);
                        var name = Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 8) + '.' + ext;
                        var dest = path.join(dir, name);
                        fs.writeFileSync(dest, Buffer.concat(chunks), { mode: 384 });
                        pruneMedia(dir);
                        cb(dest);
                    } catch (e) { log('image: save failed: ' + e.message); cb(null); }
                });
                res.on('error', function () { cb(null); });
            });
            req.on('timeout', function () { req.destroy(); });
            req.on('error', function (e) { log('image: ' + e.message); cb(null); });
        } catch (e) { log('image: ' + e.message); cb(null); }
    }

    function absolutise(base, loc) {
        try { return require('url').resolve(base, loc); } catch (e) { return loc; }
    }

    // The media folder is a cache, not a store: keep the last few snapshots only.
    function pruneMedia(dir) {
        try {
            var names = fs.readdirSync(dir).filter(function (n) { return SAFE_FILE.test(n); });
            names.sort();
            while (names.length > 8) { try { fs.unlinkSync(path.join(dir, names.shift())); } catch (e) { /* skip */ } }
        } catch (e) { /* nothing to prune */ }
    }

    // ------------------------------------------------------------------ payload

    // Validates and normalises a Home Assistant payload into the job the native
    // notifier consumes. Throws with a message on anything it cannot use.
    function buildJob(body, cfg) {
        if (!body || typeof body !== 'object' || Array.isArray(body)) throw new Error('body must be a JSON object');
        var message = shortString(body.message, MAX_MSG);
        if (!message) throw new Error('message is required');
        var job = {
            message: message,
            title: shortString(body.title, MAX_TITLE),
            image: null,
            timeout: body.timeout == null ? cfg.defaultTimeout : clampInt(body.timeout, 0, 3600, cfg.defaultTimeout),
            location: LOCATIONS.indexOf(body.location) >= 0 ? body.location : cfg.defaultLocation,
            scale: clampScale(body.sizeMultiplier),
            pauseMedia: body.pauseMedia == null ? cfg.pauseMedia : body.pauseMedia === true,
            eventUrl: 'http://127.0.0.1:' + cfg.port + '/event',
            eventToken: cfg.token,
            emitDismiss: cfg.emitDismiss,
            dismissEvent: cfg.dismissEvent,
            id: shortString(body.id, MAX_ID),
            actions: []
        };
        var given = Array.isArray(body.actions) ? body.actions.slice(0, MAX_ACTIONS) : [];
        for (var i = 0; i < given.length; i++) {
            var a = given[i];
            if (!a || typeof a !== 'object') throw new Error('actions[' + i + '] must be an object');
            var label = shortString(a.label, MAX_LABEL);
            var ev = safeEvent(a.event);
            if (!label) throw new Error('actions[' + i + '].label is required');
            if (!ev) throw new Error('actions[' + i + '].event is required');
            job.actions.push({ label: label, event: ev, context: cleanContext(a.context) });
        }
        // Dismiss is always the last action, unless the payload already provides it.
        var hasDismiss = job.actions.some(function (a) { return a.event === cfg.dismissEvent; });
        if (!hasDismiss) {
            job.actions.push({ label: cfg.dismissLabel, event: cfg.dismissEvent, context: {}, dismiss: true });
        }
        return job;
    }

    function clampScale(v) {
        var n = parseFloat(v);
        if (isNaN(n)) return 1.0;
        return Math.max(0.6, Math.min(2.5, Math.round(n * 100) / 100));
    }

    function cleanContext(c) {
        if (!c || typeof c !== 'object' || Array.isArray(c)) return {};
        var out = {}, keys = Object.keys(c).slice(0, MAX_CONTEXT_KEYS), i, k;
        for (i = 0; i < keys.length; i++) {
            k = keys[i];
            if (!/^[A-Za-z0-9._-]{1,40}$/.test(k)) continue;
            var v = c[k];
            if (typeof v === 'string') out[k] = v.slice(0, 200);
            else if (typeof v === 'number' || typeof v === 'boolean') out[k] = v;
        }
        return out;
    }

    // ------------------------------------------------------------------ showing

    // The whole show sequence: build the job, fetch the snapshot, write it atomically,
    // wake the notifier, and pause media first if asked. Calls back with an error
    // string, or null on success.
    function present(body, cfg, cb) {
        var job;
        try { job = buildJob(body, cfg); }
        catch (e) { return cb(e.message); }
        resolveImage(body.image, function (img) {
            job.image = img;
            if (body.image && !img) log('notify: image could not be fetched, showing without it');
            try { writeJobAtomically(job); }
            catch (e) { return cb('could not write the job: ' + e.message); }
            var pre = job.pauseMedia ? sendKey(cfg.pauseKey) : Promise.resolve(null);
            pre.then(function () {
                pausedForNotification = job.pauseMedia;
            }).then(function () { return poke('notify'); }).then(function () {
                detail.lastResult = 'shown';
                cb(null);
            }).catch(function (e) { cb(e.message || String(e)); });
        });
    }

    function writeJobAtomically(job) {
        var f = fds();
        var tmp = f.job + '.tmp';
        fs.writeFileSync(tmp, JSON.stringify(job), { mode: 384 });
        fs.renameSync(tmp, f.job);
    }

    function sendKey(name) {
        if (!KEY_NAME.test(name)) return Promise.resolve(null);
        return execute('/usr/bin/input_keyevent ' + name, 5000).catch(function () { return null; });
    }

    // ------------------------------------------------------------------ events

    // A button press (or the popup closing) arrives here from the notifier and is
    // relayed to Home Assistant's webhook. A dismissal also resumes media if asked.
    function relayEvent(ev, cfg, cb) {
        if (!ev || typeof ev !== 'object') return cb('event must be an object');
        var name = safeEvent(ev.event);
        if (!name) return cb('event.name is required');
        var outgoing = {
            event: name,
            id: ev.id || null,
            label: shortString(ev.label, MAX_LABEL),
            context: cleanContext(ev.context),
            source: 'popup-notifications',
            at: new Date().toISOString()
        };
        detail.lastEvent = name;
        var resume = (name === cfg.dismissEvent && pausedForNotification && cfg.resumeMedia) ? sendKey(cfg.pauseKey) : Promise.resolve(null);
        if (name === cfg.dismissEvent) pausedForNotification = false;
        resume.then(function () { postWebhook(outgoing, cfg, cb); });
    }

    function postWebhook(payload, cfg, cb) {
        if (!cfg.webhookUrl) { log('event ' + payload.event + ' (no webhookUrl set, not forwarded)'); return cb(null); }
        var data = Buffer.from ? Buffer.from(JSON.stringify(payload)) : new Buffer(JSON.stringify(payload));
        var target;
        try { target = require('url').parse(cfg.webhookUrl); } catch (e) { return cb('webhookUrl is not a URL'); }
        var mod = target.protocol === 'https:' ? https : http;
        var req = mod.request({ hostname: target.hostname, port: target.port, path: target.path, method: 'POST',
            headers: { 'Content-Type': 'application/json', 'Content-Length': data.length } }, function (res) {
            res.resume();
            if (res.statusCode >= 300) { log('webhook: HTTP ' + res.statusCode); return cb('webhook returned HTTP ' + res.statusCode); }
            cb(null);
        });
        req.on('error', function (e) { log('webhook: ' + e.message); cb(e.message); });
        req.setTimeout(8000, function () { req.destroy(); });
        req.write(data);
        req.end();
    }

    // ------------------------------------------------------------------ HTTP

    function authorised(req, cfg) {
        if (!cfg.token) return true;
        var url = req.url || '';
        var m = /[?&]token=([^&#]*)/.exec(url);
        if (m && decodeURIComponent(m[1]) === cfg.token) return true;
        return req.headers && req.headers['x-popup-token'] === cfg.token;
    }

    function readBody(req, cb) {
        var chunks = [], size = 0, done = false;
        req.on('data', function (d) {
            if (done) return;
            size += d.length;
            if (size > MAX_BODY) { done = true; cb(new Error('body too large')); req.destroy(); return; }
            chunks.push(d);
        });
        req.on('end', function () { if (!done) { done = true; cb(null, Buffer.concat(chunks).toString('utf8')); } });
        req.on('error', function (e) { if (!done) { done = true; cb(e); } });
    }

    function sendJson(res, code, obj) {
        var body = JSON.stringify(obj);
        res.writeHead(code, { 'Content-Type': 'application/json', 'X-Content-Type-Options': 'nosniff' });
        res.end(body);
    }

    function handle(req, res, cfg) {
        var route = ((req.url || '').split('?')[0] || '/').replace(/\/+$/, '') || '/';
        if (!authorised(req, cfg)) return sendJson(res, 401, { error: 'unauthorised' });

        if (req.method === 'GET' && (route === '/' || route === '/status')) {
            return sendJson(res, 200, { ok: true, running: detail.running, port: cfg.port,
                webhook: cfg.webhookUrl ? 'set' : 'unset', last: detail.lastResult, error: detail.error });
        }
        if (req.method === 'POST' && route === '/notify') {
            return readBody(req, function (err, text) {
                if (err) return sendJson(res, 413, { error: err.message });
                var body;
                try { body = JSON.parse(text || '{}'); } catch (e) { return sendJson(res, 400, { error: 'invalid JSON' }); }
                present(body, cfg, function (problem) {
                    if (problem) return sendJson(res, 400, { error: problem });
                    sendJson(res, 200, { ok: true });
                });
            });
        }
        if (req.method === 'POST' && route === '/dismiss') {
            return poke('hide').then(function () { sendJson(res, 200, { ok: true }); })
                .catch(function (e) { sendJson(res, 500, { error: e.message }); });
        }
        if (req.method === 'POST' && route === '/event') {
            return readBody(req, function (err, text) {
                if (err) return sendJson(res, 413, { error: err.message });
                var ev;
                try { ev = JSON.parse(text || '{}'); } catch (e) { return sendJson(res, 400, { error: 'invalid JSON' }); }
                relayEvent(ev, cfg, function (problem) {
                    // The event is already logged/attempted: never make the notifier wait on it.
                    sendJson(res, problem ? 502 : 200, problem ? { error: problem } : { ok: true });
                });
            });
        }
        sendJson(res, 404, { error: 'not found' });
    }

    function openServer(cfg, cb) {
        try { server = http.createServer(function (req, res) {
            try { handle(req, res, cfg); }
            catch (e) { log('serve: ' + e.message); try { sendJson(res, 500, { error: 'internal' }); } catch (e2) { /* gone */ } }
        }); } catch (e) { return cb(e); }
        server.on('error', function (e) { detail.listening = false; cb(e); });
        server.listen(cfg.port, cfg.bind, function () { detail.listening = true; detail.port = cfg.port; cb(null); });
    }

    function closeServer() {
        if (!server) return;
        try { server.close(); } catch (e) { /* already closing */ }
        server = null;
        detail.listening = false;
    }

    // ------------------------------------------------------------------ routes

    // The Shard's page (in the dashboard) reads and writes the config here. The
    // notification listener is a separate port and not part of this.
    function routeConfig(req, res, sub) {
        // The dashboard page. Vetro asks for "ui" because the manifest sets ui.label, and
        // expects the Shard to serve the HTML itself (docs/plugins.md).
        if (sub === 'ui' && req.method === 'GET') {
            try {
                var html = fs.readFileSync(path.join(__dirname, 'ui.html'));
                res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'X-Content-Type-Options': 'nosniff' });
                res.end(html);
            } catch (e) {
                log('ui: ' + e.message);
                sendJson(res, 500, { error: 'ui.html is missing from the bundle' });
            }
            return true;
        }
        if (sub === 'debug' && req.method === 'POST') {
            // A compositing check the page can trigger: paint the whole screen. If it shows,
            // the notification window draws over the app and any blank popup is a drawing
            // problem; if not, the window is not being shown on top.
            readBody(req, function (err, text) {
                if (err) return sendJson(res, 413, { error: err.message });
                var body = {};
                try { body = JSON.parse(text || '{}'); } catch (e) { /* optional */ }
                var cfg = config(), f = fds();
                var job = {
                    message: 'Solid overlay test', title: 'Debug', image: null,
                    solid: (body.mode || 'solid') === 'solid',
                    timeout: typeof body.timeout === 'number' ? body.timeout : 6,
                    location: 'center', scale: 1, pauseMedia: false,
                    eventUrl: 'http://127.0.0.1:' + cfg.port + '/event', eventToken: cfg.token,
                    emitDismiss: false, dismissEvent: cfg.dismissEvent, id: 'debug',
                    actions: [{ label: cfg.dismissLabel, event: cfg.dismissEvent, context: {}, dismiss: true }]
                };
                try { writeJobAtomically(job); }
                catch (e) { return sendJson(res, 500, { error: 'could not write the job: ' + e.message }); }
                poke('notify').then(function () { sendJson(res, 200, { ok: true }); })
                    .catch(function (e) { sendJson(res, 500, { error: e.message }); });
            });
            return true;
        }
        if (sub === 'logs' && req.method === 'GET') {
            var lines = 150;
            var m = /[?&]lines=(\d+)/.exec(req.url || '');
            if (m) lines = Math.max(10, Math.min(500, parseInt(m[1], 10) || 150));
            var cfg = config(), f = fds();
            var out = {
                status: { running: detail.running, listening: detail.listening, port: detail.port,
                    lastResult: detail.lastResult, lastEvent: detail.lastEvent, error: detail.error },
                config: { port: cfg.port, bind: cfg.bind, webhook: cfg.webhookUrl ? 'set' : 'unset',
                    token: cfg.token ? 'set' : 'unset', keyMode: cfg.keyMode, homeKeyCode: cfg.homeKeyCode,
                    emitDismiss: cfg.emitDismiss, pauseKey: cfg.pauseKey, resumeMedia: cfg.resumeMedia,
                    defaultTimeout: cfg.defaultTimeout, defaultLocation: cfg.defaultLocation },
                dll: fileInfo(f.dll),
                job: null,
                notifier: tail(path.join(f.dir, 'notifier.log'), lines),
                shard: tail(path.join(f.dir, 'popup.log'), lines)
            };
            try { out.job = JSON.parse(fs.readFileSync(f.job, 'utf8')); } catch (e) { /* none yet */ }
            if (out.notifier === null) {
                // The log is root-owned; read it through the agent if the service user cannot.
                execute('tail -n ' + lines + ' ' + q(path.join(f.dir, 'notifier.log')) + ' 2>/dev/null || true', 6000).then(function (r) {
                    out.notifier = (r && r.stdout ? r.stdout.replace(/\r/g, '').replace(/\n$/, '').split('\n') : ['(notifier.log not readable)']);
                    sendJson(res, 200, out);
                }).catch(function () { out.notifier = ['(notifier.log not readable)']; sendJson(res, 200, out); });
                return true;
            }
            sendJson(res, 200, out);
            return true;
        }
        if (sub === 'config' && req.method === 'GET') {
            var raw = {};
            try { raw = ctx.config.get() || {}; } catch (e) { raw = {}; }
            var merged = {}, k;
            for (k in DEFAULTS) if (DEFAULTS.hasOwnProperty(k)) merged[k] = raw[k] === undefined ? DEFAULTS[k] : raw[k];
            sendJson(res, 200, { config: merged, defaults: DEFAULTS, state: detail });
            return true;
        }
        if (sub === 'config' && req.method === 'POST') {
            readBody(req, function (err, text) {
                if (err) return sendJson(res, 413, { error: err.message });
                var body;
                try { body = JSON.parse(text || '{}'); } catch (e) { return sendJson(res, 400, { error: 'invalid JSON' }); }
                // Merge over what is stored: config.set replaces the whole file, and a
                // page that saves one field must not wipe the rest.
                var stored = {};
                try { stored = ctx.config.get() || {}; } catch (e) { stored = {}; }
                var k;
                for (k in DEFAULTS) if (DEFAULTS.hasOwnProperty(k) && body[k] !== undefined) stored[k] = body[k];
                try { ctx.config.set(stored); }
                catch (e) { return sendJson(res, 500, { error: e.message }); }
                var cfg = config();
                restartServer(cfg);
                restartNotifier();
                sendJson(res, 200, { ok: true, config: cfg });
            });
            return true;
        }
        if (sub === 'test' && req.method === 'POST') {
            readBody(req, function (err, text) {
                if (err) return sendJson(res, 413, { error: err.message });
                var body;
                try { body = JSON.parse(text || '{}'); } catch (e) { return sendJson(res, 400, { error: 'invalid JSON' }); }
                present(body, config(), function (problem) {
                    sendJson(res, problem ? 400 : 200, problem ? { error: problem } : { ok: true });
                });
            });
            return true;
        }
        return false;
    }

    function restartServer(cfg) {
        closeServer();
        openServer(cfg, function (e) { if (e) { log('listen: ' + e.message); detail.error = e.message; } });
    }

    // ------------------------------------------------------------------ lifecycle

    return {
        start: function () {
            if (active) return;
            active = true;
            generation++;
            var cfg = config();
            detail.lastResult = 'starting';
            restartServer(cfg);
            check(cfg);
            timer = setInterval(function () { check(config()); }, TICK_MS);
            if (timer.unref) timer.unref();
        },
        stop: function () {
            active = false;
            generation++;
            if (timer) clearInterval(timer);
            timer = null;
            closeServer();
            detail.lastResult = 'stopped';
            detail.running = false;
            return Promise.resolve().then(function () { return execute(stopCommand(), 10000); }).catch(function () {});
        },
        status: function () {
            return { running: detail.running, listening: detail.listening, port: detail.port,
                lastResult: detail.lastResult, lastEvent: detail.lastEvent, error: detail.error };
        },
        route: function (req, res, sub) {
            try { return routeConfig(req, res, sub); }
            catch (e) { log('route: ' + e.message); sendJson(res, 500, { error: 'internal' }); return true; }
        }
    };
};

// Exported for the tests / for a reader: the pieces that carry the rules.
module.exports.DEFAULTS = DEFAULTS;
module.exports.LOCATIONS = LOCATIONS;
