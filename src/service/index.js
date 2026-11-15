/*
 * Launch helper: a Tizen Web service (declared in config.xml, started by the page) that opens the
 * small class of Samsung apps only Smart Hub is allowed to open.
 *
 * Why it exists — BBC iPlayer (and similar broadcaster "hybrid" shells) accept a
 * tizen.application.launch and then silently bounce you to Smart Hub's home screen, with no error
 * to catch. The one channel that does open them is the TV's own developer connection (sdbd):
 * `0 was_execute <app id>`. A web page cannot reach sdbd (there is no raw-socket web API on this
 * engine), so this background service does it and the page talks to the service.
 *
 * It is deliberately narrow. The loopback endpoint can do exactly one thing: run `0 was_execute`
 * for an app id on the list below. No shell, no caller-supplied command, no arguments from the
 * page but the id — and only an id that is already allowed. That is the whole security story:
 * another app on the TV that reaches this port can, at worst, open one of these apps.
 *
 * It reaches sdbd the same ways a computer does: directly on 127.0.0.1:26101 or this TV's own
 * address on 26101 (Developer Mode's Host PC IP must name one of those two), or through Vetro's SDB
 * proxy on 26201 if Vetro is on the TV. Whichever answers first is used. Nothing else about the TV
 * is changed, and nothing is written to it.
 *
 * Node built-ins only, ES5, one file (no bundler): the service runtime on a TV is an old Node, and
 * relative requires across the package are not worth relying on. The SDB wire protocol is ADB's:
 * a 24-byte header — command, arg0, arg1, data length, a byte-sum "checksum", and command XOR
 * 0xFFFFFFFF, all little-endian uint32 — then the data. Based on Android's adb protocol.txt; the
 * same sequence TizenBrew, Apps2Samsung TV-Store and free-my-tv's SDB client use.
 */
'use strict';

var http = require('http');
var net = require('net');

var HOST = '127.0.0.1';
var PORT = 8078;            // must match js/config.js -> helper.url
var SDB_HOST = '127.0.0.1';
var SDB_PORT = 26101;
var SDB_PROXY_PORT = 26201;   // Vetro's SDB tunnel, when Vetro is on the TV
var MAX_DATA = 4096;
var LAUNCH_TIMEOUT_MS = 15000;
var IO_TIMEOUT_MS = 5000;

// The only apps this will open, and only via `0 was_execute`. Keep in step with
// js/config.js -> helper.apps (the page routes only these to the helper).
var ALLOWED = {
    'DqEoaplKlw.bbchybrid': 'BBC iPlayer'
};

// ---------------------------------------------------------------------------
// Small helpers for an old Node
// ---------------------------------------------------------------------------

function buf(size, fill) {
    if (Buffer.alloc) return fill ? Buffer.alloc(size, fill) : Buffer.alloc(size);
    return fill ? new Buffer(size, fill) : new Buffer(size);
}
function bufFrom(str) {
    return Buffer.from ? Buffer.from(str, 'binary') : new Buffer(str, 'binary');
}
function alloc(n) { return buf(n); }

var COMMAND = {};
['SYNC', 'OPEN', 'CNXN', 'AUTH', 'OKAY', 'CLSE', 'WRTE'].forEach(function (name) {
    COMMAND[name] = bufFrom(name).readUInt32LE(0);
});

function checksum(buffer) {
    var sum = 0;
    for (var i = 0; i < buffer.length; i++) sum = (sum + buffer[i]) & 0xFFFFFFFF;
    return sum >>> 0;
}

// ---------------------------------------------------------------------------
// A minimal SDB client (the pieces a single `shell:` command needs)
// ---------------------------------------------------------------------------

function Sdb(host, port) {
    this.host = host || SDB_HOST;
    this.port = port || SDB_PORT;
    this.sock = null;
    this.leftover = alloc(0);
    this.nextId = 1;
    this.okay = {};         // local id -> remote id
    this.received = {};     // local id -> Buffer
    this.closed = {};       // local id -> true
    this.waiters = [];
    this.connected = false;
}

Sdb.prototype.packet = function (command, arg0, arg1, data) {
    var payload = data || alloc(0);
    var out = alloc(24 + payload.length);
    out.writeUInt32LE(command, 0);
    out.writeUInt32LE(arg0 >>> 0, 4);
    out.writeUInt32LE(arg1 >>> 0, 8);
    out.writeUInt32LE(payload.length, 12);
    out.writeUInt32LE(checksum(payload), 16);
    out.writeUInt32LE((command ^ 0xFFFFFFFF) >>> 0, 20);
    if (payload.length) payload.copy(out, 24);
    return out;
};

Sdb.prototype.send = function (command, arg0, arg1, data) {
    if (this.sock) this.sock.write(this.packet(command, arg0, arg1, data));
};

Sdb.prototype.connect = function () {
    var self = this;
    return new Promise(function (resolve, reject) {
        var settled = false;
        function fail(err) { if (!settled) { settled = true; reject(err); } }

        var sock = net.connect(self.port, self.host);
        self.sock = sock;
        sock.on('error', fail);
        sock.on('close', function () { fail(new Error('the TV closed the SDB connection')); });
        sock.on('data', function (chunk) {
            self.leftover = Buffer.concat([self.leftover, chunk]);
            self.drain();
        });
        sock.on('connect', function () {
            self.send(COMMAND.CNXN, 0x01000000, MAX_DATA, bufFrom('host::\0'));
            self.await(function () { return self.connected; }, IO_TIMEOUT_MS).then(function (ok) {
                if (ok) { settled = true; resolve(); }
                else fail(new Error('sdbd did not answer the connect'));
            });
        });
    });
};

Sdb.prototype.drain = function () {
    while (this.leftover.length >= 24) {
        var command = this.leftover.readUInt32LE(0);
        var arg0 = this.leftover.readUInt32LE(4);
        var arg1 = this.leftover.readUInt32LE(8);
        var length = this.leftover.readUInt32LE(12);
        if (this.leftover.length < 24 + length) break;
        var data = this.leftover.slice(24, 24 + length);
        this.leftover = this.leftover.slice(24 + length);
        this.accept(command, arg0, arg1, data);
    }
    this.wake();
};

Sdb.prototype.accept = function (command, arg0, arg1, data) {
    if (command === COMMAND.CNXN) {
        if (arg1) this.maxData = arg1;
        this.connected = true;
    } else if (command === COMMAND.OKAY) {
        this.okay[arg1] = arg0;
    } else if (command === COMMAND.WRTE) {
        var local = arg1;
        this.received[local] = Buffer.concat([this.received[local] || alloc(0), data]);
        this.send(COMMAND.OKAY, local, arg0, null);
    } else if (command === COMMAND.CLSE) {
        this.closed[arg1] = true;
    }
};

Sdb.prototype.wake = function () {
    var waiters = this.waiters.slice();
    for (var i = 0; i < waiters.length; i++) waiters[i]();
};

Sdb.prototype.await = function (predicate, ms) {
    var self = this;
    return new Promise(function (resolve) {
        var timer = setTimeout(function () { stop(); resolve(false); }, ms);
        function stop() {
            clearTimeout(timer);
            var i = self.waiters.indexOf(check);
            if (i >= 0) self.waiters.splice(i, 1);
        }
        function check() { if (predicate()) { stop(); resolve(true); } }
        self.waiters.push(check);
        check();
    });
};

Sdb.prototype.open = function (service) {
    var self = this;
    var local = this.nextId++;
    this.send(COMMAND.OPEN, local, 0, bufFrom(service + '\0'));
    return this.await(function () {
        return self.okay[local] !== undefined || self.closed[local];
    }, IO_TIMEOUT_MS).then(function (ok) {
        if (!ok || self.closed[local] || self.okay[local] === undefined) {
            throw new Error('sdbd refused ' + service);
        }
        var remote = self.okay[local];
        delete self.okay[local];
        return { local: local, remote: remote };
    });
};

Sdb.prototype.shell = function (command, done, ms) {
    var self = this;
    return this.open('shell:' + command).then(function (stream) {
        function text() {
            var b = self.received[stream.local];
            return b ? b.toString('utf8') : '';
        }
        return self.await(function () {
            return self.closed[stream.local] || (done && done(text()));
        }, ms).then(function () {
            var out = text();
            delete self.received[stream.local];
            return out;
        });
    });
};

Sdb.prototype.close = function () {
    try { if (this.sock) this.sock.end(); } catch (e) { /* already gone */ }
};

// ---------------------------------------------------------------------------
// Opening an app over the developer connection
// ---------------------------------------------------------------------------

// Where to reach sdb, most direct first. The TV's own sdbd accepts a connection only from the
// address Developer Mode's Host PC IP names (either 127.0.0.1 or this TV's own LAN address), so both
// are tried. Vetro, if it is on the TV, keeps its own sdbd connection open and shares it as an SDB
// proxy on 26201 — which reaches sdbd from a source it accepts without Developer Mode naming
// anything — so that is tried next, on both addresses too. `extra` is this TV's own address, read by
// the page, where systeminfo is known to work (the service runtime may not expose it).
function candidates(extra) {
    var hosts = ['127.0.0.1'];
    (extra || []).forEach(function (host) {
        if (host && hosts.indexOf(host) < 0) hosts.push(host);
    });
    var list = [];
    hosts.forEach(function (host) { list.push({ host: host, port: SDB_PORT }); });
    hosts.forEach(function (host) { list.push({ host: host, port: SDB_PROXY_PORT }); });
    return list;
}

function describeTargets(extra) {
    return candidates(extra).map(function (c) { return c.host + ':' + c.port; }).join(', ');
}

function wasExecute(id, extra, cb) {
    tryCandidate(candidates(extra), 0, id, cb);
}

function tryCandidate(list, index, id, cb) {
    if (index >= list.length) {
        var where = list.map(function (c) { return c.host + ':' + c.port; }).join(', ');
        return cb(new Error('could not reach sdb on ' + where
            + '. Check Developer Mode (Host PC IP set to this TV\'s address or 127.0.0.1, and the TV restarted after),'
            + ' or that Vetro is on the TV with its SDB proxy running.'));
    }
    var target = list[index];
    var sdb = new Sdb(target.host, target.port);
    sdb.connect().then(function () {
        return sdb.shell('0 was_execute ' + id, /launched|spend time/, LAUNCH_TIMEOUT_MS);
    }).then(function (out) {
        sdb.close();
        cb(null, out, target.host + ':' + target.port);
    }, function (err) {
        sdb.close();
        // A wrong address (or a proxy that is not up) drops the connection with no error: try the next.
        tryCandidate(list, index + 1, id, cb);
    });
}

// ---------------------------------------------------------------------------
// The loopback endpoint the page talks to
// ---------------------------------------------------------------------------

function reply(res, status, body) {
    var text = JSON.stringify(body);
    res.writeHead(status, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(text) });
    res.end(text);
}

function readJson(req, cb) {
    var chunks = [];
    var size = 0;
    req.on('data', function (chunk) {
        size += chunk.length;
        if (size > 4096) { req.destroy(); return; }
        chunks.push(chunk);
    });
    req.on('end', function () {
        try {
            cb(null, JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}'));
        } catch (e) {
            cb(e);
        }
    });
}

var busy = false;
var last = null;           // the last launch: { at, id, ok, output | error }, reported by /ping
var server = null;

function handle(req, res) {
    var path = req.url.split('?')[0];

    if (req.method === 'GET' && path === '/ping') {
        var names = [];
        Object.keys(ALLOWED).forEach(function (id) { names.push(id); });
        return reply(res, 200, { ok: true, service: 'arn0-launch-helper', allowed: names, busy: busy, last: last });
    }

    if (req.method === 'POST' && path === '/launch') {
        return readJson(req, function (err, body) {
            if (err) return reply(res, 400, { ok: false, error: 'body was not JSON' });
            var id = body && body.id;
            if (!id || !Object.prototype.hasOwnProperty.call(ALLOWED, id)) {
                return reply(res, 400, { ok: false, error: 'not an app this helper opens' });
            }
            if (busy) return reply(res, 409, { ok: false, error: 'another launch is running' });
            busy = true;
            var extra = [].concat(body.hosts || [], body.host || []);
            console.log('[arn0-helper] was_execute ' + id + ' — targets ' + describeTargets(extra));
            wasExecute(id, extra, function (err, out, host) {
                busy = false;
                if (err) {
                    var reason = String(err.message || err);
                    console.log('[arn0-helper] failed: ' + reason);
                    last = { at: Date.now(), id: id, ok: false, error: reason };
                    return reply(res, 500, { ok: false, error: reason });
                }
                var text = String(out || '').slice(0, 400);
                last = { at: Date.now(), id: id, ok: true, host: host, output: text };
                reply(res, 200, { ok: true, app: ALLOWED[id], host: host, output: text });
            });
        });
    }

    reply(res, 404, { ok: false, error: 'not found' });
}

module.exports.onStart = function () {
    console.log('[arn0-helper] starting on ' + HOST + ':' + PORT);
    server = http.createServer(handle);
    server.on('error', function (err) {
        console.log('[arn0-helper] server error: ' + (err.message || err));
    });
    server.listen(PORT, HOST, function () {
        console.log('[arn0-helper] listening');
    });
};

module.exports.onStop = function () {
    if (server) { try { server.close(); } catch (e) { /* already gone */ } }
};
