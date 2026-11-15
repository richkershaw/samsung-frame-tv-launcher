/*
 * Page side of the launch helper (the bundled Web service in src/service/index.js).
 *
 * Some Samsung apps — BBC iPlayer is one, as are other broadcasters' "hybrid" shells — can only be
 * opened by Smart Hub. `tizen.application.launch` is accepted and then silently bounces to Smart
 * Hub's home screen, so there is no error to show and nothing to retry. The helper service opens
 * them over the TV's own developer connection instead. This starts that service and asks it.
 *
 * Nothing here runs in the browser preview (there is no service to reach): the mock makes
 * isHelperApp() false, so preview launches go the ordinary way.
 */
(function () {
    'use strict';

    var cfg = (window.LAUNCHER_CONFIG && window.LAUNCHER_CONFIG.helper) || {};
    var BASE = cfg.url || 'http://127.0.0.1:8078';
    var APPS = {};
    (cfg.apps || []).forEach(function (id) { APPS[id] = true; });
    var FIXED_HOSTS = (cfg.hosts || []).slice();

    var asked = false;      // the service has been started at least once this run

    function serviceId() {
        try {
            return tizen.application.getCurrentApplication().appInfo.id.split('.')[0] + '.LaunchService';
        } catch (e) {
            return 'LKLauncher.LaunchService';
        }
    }

    function send(method, path, body, timeoutMs) {
        return new Promise(function (resolve, reject) {
            var req = new XMLHttpRequest();
            req.open(method, BASE + path, true);
            req.timeout = timeoutMs || 6000;
            req.onreadystatechange = function () {
                if (req.readyState !== 4) return;
                var reply = null;
                try { reply = JSON.parse(req.responseText); } catch (e) { /* no body */ }
                if (req.status === 200) return resolve(reply);
                // status 0 means the service is not up (yet); anything else carries a reason.
                if (req.status === 0) return reject(new Error('the launch helper is not answering'));
                reject(new Error((reply && reply.error) || ('the launch helper answered HTTP ' + req.status)));
            };
            req.ontimeout = function () { reject(new Error('the launch helper did not answer')); };
            req.onerror = function () { reject(new Error('the launch helper is not answering')); };
            if (body) {
                req.setRequestHeader('Content-Type', 'application/json');
                req.send(JSON.stringify(body));
            } else {
                req.send();
            }
        });
    }

    // Start the service the way a pre-Tizen-9 TV does it: an app control for the service operation,
    // with a plain launch as the fallback. Both "succeed" as far as the page can tell — the service
    // answers /ping only once it is really up, which is what startAndPing waits for.
    function startService() {
        if (asked) return Promise.resolve();
        asked = true;
        return new Promise(function (resolve) {
            function plain() {
                try {
                    tizen.application.launch(serviceId(), function () { resolve(); }, function () { resolve(); });
                } catch (e) {
                    resolve();
                }
            }
            try {
                tizen.application.launchAppControl(
                    new tizen.ApplicationControl('http://tizen.org/appcontrol/operation/service'),
                    serviceId(),
                    function () { resolve(); },
                    function () { plain(); });
            } catch (e) {
                plain();
            }
        });
    }

    function ping() {
        return send('GET', '/ping');
    }

    // This TV's own LAN address, or null. The service needs it: sdbd (and Vetro's proxy) accept a
    // connection by address, and on some TVs a loopback connection to another app's port is refused,
    // so the TV's own address is the one that gets in. systeminfo is the same call the rest of the
    // app uses for its IP; a short timeout keeps a silent systeminfo from holding up a launch.
    function tvAddress() {
        return new Promise(function (resolve) {
            var done = false;
            function finish(ip) { if (!done) { done = true; resolve(ip || null); } }
            setTimeout(function () { finish(null); }, 1500);
            try {
                ['WIFI', 'ETHERNET'].forEach(function (profile) {
                    tizen.systeminfo.getPropertyValue(profile,
                        function (info) { if (info && info.ipAddress) finish(info.ipAddress); },
                        function () {});
                });
            } catch (e) {
                finish(null);
            }
        });
    }

    // The first start after an install is slow (the platform can take a while to bring a service up),
    // so poll: ~10 s before giving up. Later calls find it already answering.
    function startAndPing(tries) {
        tries = tries || 0;
        return startService()
            .then(function () { return ping(); })
            .catch(function (err) {
                if (tries >= 20) throw err;
                return new Promise(function (r) { setTimeout(r, 500); })
                    .then(function () { return startAndPing(tries + 1); });
            });
    }

    // True for apps the helper should open. False in the preview, where there is no service.
    function isHelperApp(id) {
        return !window.__TIZEN_MOCK__ && !!APPS[id];
    }

    function launch(id) {
        return startAndPing()
            .then(function () { return tvAddress(); })
            .then(function (found) {
                var hosts = FIXED_HOSTS.slice();
                if (found && hosts.indexOf(found) < 0) hosts.push(found);
                return send('POST', '/launch', { id: id, hosts: hosts }, 30000);
            });
    }

    function actions() {
        var first = (cfg.apps || [])[0];
        var out = [{
            label: 'Launch helper: is the service up?',
            run: function () {
                ping().then(function (r) {
                    DevLog.log('Launch helper ping: ' + JSON.stringify(r));
                    UI.toast('Launch helper is up');
                }, function (err) {
                    DevLog.log('Launch helper ping failed: ' + DevLog.errorText(err));
                    UI.toast('Launch helper: ' + DevLog.errorText(err));
                });
            }
        }];
        if (first) {
            out.push({
                label: 'Launch helper: open ' + first + ' over the developer connection',
                run: function () {
                    DevLog.log('Launch helper: opening ' + first);
                    launch(first).then(function (r) {
                        DevLog.log('Launch helper: ' + JSON.stringify(r));
                        UI.toast('Asked the launch helper to open it');
                    }, function (err) {
                        DevLog.log('Launch helper failed: ' + DevLog.errorText(err));
                        UI.toast('Launch helper: ' + DevLog.errorText(err));
                    });
                }
            });
        }
        return out;
    }

    window.LaunchHelper = {
        isHelperApp: isHelperApp,
        launch: launch,
        ping: ping,
        actions: actions
    };
})();
