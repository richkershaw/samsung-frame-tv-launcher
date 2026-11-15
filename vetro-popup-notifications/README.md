# Popup notifications — a Vetro Shard

Draw a popup **over whatever app is on screen** on a rooted Samsung Tizen TV, from any
program that can POST JSON: a message, an optional snapshot image, and up to four buttons
that POST back to a webhook. Styled after Tizen's own popups (top-right by default).

```text
any caller ──HTTP──▶ Shard (JS, in the Vetro service) ──FIFO──▶ notify.dll (native EFL, root)
     ▲                                                              │
     └──────────────── HTTP event (webhook) ◀───────────────────────┘
```

## This is not Home Assistant-specific

There is nothing HA-shaped in the protocol. **In** is `POST /notify` with a JSON body; **out**
is a JSON `POST` to whatever webhook URL you configure. Home Assistant is one convenient
caller and is used as the example below, but the same thing works from Node, a Python script,
an ESPHome device, IFTTT, a shell `curl`, or another automation system. HA is only where the
sample YAML happens to come from.

## Why there are two halves

The same split Vetro's launcher and the [8BitDo Guide](https://github.com/andrearicci93/vetro-plugins)
Shard use, for the same reason: **a Shard cannot draw on the TV.** A Shard's `ctx` has `id`,
`dir`, `config` and `execute`, plus a page that lives in the Vetro dashboard in a browser —
there is no window. Only a native process can put pixels over another app.

- **The Shard** (`popup-notifications/`, JavaScript): an HTTP listener, the snapshot download,
  the notifier's supervision, and the relay of button presses onward.
- **`notify.dll`** (`native/`, C#/.NET, run by the TV's own `/usr/bin/dotnet` as root):
  creates an EFL notification window (`ELM_WIN_NOTIFICATION`, level 40, layer 900 — the
  launcher's trick) and draws the card, and reads the remote. Ships in the Shard bundle as
  `notify.dll.gz.b64`, because a bundle is text-only.

## Requirements

- A **rooted** TV running Vetro with the root agent connected. Without root, none of the
  overlay exists.
- The TV's .NET Core 3.1 runtime at `/usr/bin/dotnet` (Vetro's launcher already relies on it).
- Tizen 6–9 on the tested firmware. Tizen 10 has no root yet, so no overlay.
- The caller able to reach the TV, and the TV able to reach the webhook. Both directions are
  plain HTTP; keep the TV on a trusted LAN and never expose the listener to the internet. Set
  a token for a second lock.

## Install

1. **Build the native notifier** (needs the .NET SDK with the netcoreapp3.1 targeting pack):

   ```sh
   ./scripts/build-native.sh
   ```

2. **Pack the Shard** with Vetro's own tool, from a Vetro source checkout:

   ```sh
   node scripts/vetro-plugin.cjs pack /path/to/vetro-popup-notifications/popup-notifications
   ```

3. Install the `.vplugin` in **Vetro Shards → Install from a file** (needs Developer mode, since
   this is unsigned), switch it **on**, open its **Popups** page and set the port and webhook.
   Use **Test** to show a popup immediately.

## Settings — a worked example

On the Shard's page:

| Setting | Value | |
| --- | --- | --- |
| `port` | `8079` | What `/notify` listens on |
| `bind` | `0.0.0.0` | LAN only |
| `token` | `s3cret` (or blank) | Requests then need `?token=s3cret` or `X-Popup-Token` |
| `webhookUrl` | `http://homeassistant.local:8123/api/webhook/tv-popups` | Where button presses go |
| `emitDismiss` | on | Also send an event when the popup closes |
| `dismissEvent` / `dismissLabel` | `dismiss` / `Dismiss` | The always-present last button |
| `defaultTimeout` / `defaultLocation` | `60` / `tr` | Seconds (0 = stay) and position |
| `pauseMedia` / `resumeMedia` / `pauseKey` | off / off / `XF86AudioPlay` | |
| `keyMode` | `auto` | See **Remote keys** |
| `homeKeyCode` | `0` | Optional extra dismiss code |

Then show one straight away:

```sh
curl -sS -X POST "http://192.168.1.50:8079/notify?token=s3cret" \
  -H 'content-type: application/json' \
  -d '{"title":"Front door","message":"Someone is at the front door","timeout":45,
       "pauseMedia":true,"location":"tr","sizeMultiplier":1.2,
       "image":"http://homeassistant.local:8123/api/camera_proxy/camera.front_door?token=…",
       "actions":[{"label":"Unlock","event":"front_door_unlock","context":{"lock":"front_door"}}]}'
```

The full payload is in [docs/PROTOCOL.md](docs/PROTOCOL.md).

## Home Assistant recipe

```yaml
# configuration.yaml
rest_command:
  tv_popup:
    url: "http://192.168.1.50:8079/notify?token=s3cret"
    method: POST
    content_type: "application/json"
    payload: "{{ payload }}"
```

**Show a popup** (the blueprint is only a shortcut for this):

```yaml
# automations.yaml
- alias: Front door → TV popup
  triggers:
    - trigger: state
      entity_id: binary_sensor.front_doorbell
      to: "on"
  actions:
    - action: rest_command.tv_popup
      data:
        payload: >-
          {"title": "Front door",
           "message": "Someone is at the front door",
           "pauseMedia": true,
           "timeout": 45,
           "location": "tr",
           "image": "http://homeassistant.local:8123{{ state_attr('camera.front_door', 'entity_picture') }}",
           "actions": [
             {"label": "View live", "event": "front_door_view", "context": {"camera": "front_door"}},
             {"label": "Unlock", "event": "front_door_unlock", "context": {"lock": "front_door"}}
           ]}
```

`entity_picture` is a short-lived signed URL, so no token is needed in the payload.

**Act on a button** — a second, independent automation. It receives the webhook the Shard
posts to and branches on the event name. `webhook_id` is any string you choose; Home Assistant
then exposes it at `http://<ha>/api/webhook/<webhook_id>`, which is what you put in the Shard's
`webhookUrl` setting. The automation must exist and be loaded for that URL to answer.

```yaml
# automations.yaml  (both this and the entry above go in the same list)
- alias: TV popup → act
  triggers:
    - trigger: webhook
      webhook_id: "tv-popups"          # the <webhook_id> in the Shard's webhookUrl
      allowed_methods: [POST]
      local_only: true
  actions:
    - choose:
        - conditions: "{{ trigger.json.event == 'front_door_unlock' }}"
          sequence:
            - action: lock.unlock
              target: { entity_id: lock.front_door }
        - conditions: "{{ trigger.json.event == 'front_door_view' }}"
          sequence:
            - action: camera.turn_on
              target: { entity_id: "{{ trigger.json.context.camera }}" }
```

Both `automations.yaml` snippets above are entries in one list: paste them under the existing
`- alias:` lines, not inside each other. In the UI, **Settings → Automations → Create → Edit in
YAML** takes a single automation *without* the leading `- ` — that dash is what makes it a list
item in the file, and while useful when pasting into the file it is the thing that makes these
blocks look like fragments out of context.

**Or use the blueprint**: copy
[`blueprints/automation/popup_notifications/popup_notification.yaml`](blueprints/automation/popup_notifications/popup_notification.yaml)
to `config/blueprints/automation/popup_notifications/`, reload automations, and pick
**TV popup notification**. It fires on an **event entity** (domain `event` — a doorbell, a
button): choose the entity and the **event type** it reports (e.g. `ring`), then the message,
camera, position and one button. It assumes a `rest_command.tv_popup` exists, as above.

The hand-written example above uses a state trigger on a `binary_sensor`; if your doorbell is
an event entity instead, that is the trigger the blueprint uses — `trigger: event.received`
with a `target` entity and an `event_type` option.

## Remote keys

`keyMode` decides where key presses come from:

| Mode | Behaviour |
| --- | --- |
| `auto` (default) | Reads the remote's input nodes **read-only** (never grabbing) and drops the copy the focused window also receives, exactly as the launcher does. If no node can be opened it falls back to the window's own key events. |
| `evas` | Only keys the popup window receives. Use if you want the compositor path alone. |
| `evdev` | Read the remote and ignore the window entirely. **Use this if the remote does not reach the popup on your firmware** — the case a focused window is not given. |
| `grab` | Like `auto`, but takes the remote **exclusively** (`EVIOCGRAB`) while a popup is up and releases it on hide, so a key the popup handles is not also delivered to the app behind it. |

`auto`, `evdev` and `grab` read the remote without consuming it, so a key is seen by both the
popup and the app — Back closes the popup *and* goes back in the app. `grab` removes that: the
popup owns the remote while it is on screen. The cost is that keys the popup does not map
(Left/Right/OK/Back, plus `homeKeyCode` if set) are swallowed while it is up. A grab that fails
because another process holds the device is logged and ignored, and the remote is released when
the popup hides or the process ends, so a crash cannot leave it locked.

Left/Right move between buttons, OK presses, Back or Home dismisses, and `1`–`9` press a
button directly. `homeKeyCode` (raw evdev code, `0` = off) adds a specific key that also
dismisses; on Vetro's forwarded remote Home is often `63`.

## Known-good and still unverified

Confirmed on a **Samsung The Frame, QE65LS03BAUXXU (2022), Tizen 6.5, firmware
T-PTMDEUC-1720.7**, on Vetro 0.5.x:

- The popup draws over the running app — the card, pills, focus ring, title, message and
  snapshot all render, and the window takes focus.
- Remote keys work (Left/Right move, OK presses, Back/Home dismiss), both read-only and with
  `keyMode: grab`, which stops the app behind from also seeing the keys.
- The generated raw-ARGB images render with the expected colours, so the channel order is right
  for this engine.

Still worth checking on your TV:

- **Window values** (`ELM_WIN_NOTIFICATION` = 12, level 40, layer 900, the
  `wm.policy.win.user.geometry` hint) are the launcher's; a different model may need them nudged.
- **Over DRM-protected video** an overlay may not composite. Over a normal app, HDMI or the
  launcher it does.
- **`pauseKey`** is a guess: `XF86AudioPlay` is an X keysym; confirm it toggles play/pause for
  your source. `resumeMedia` re-sends it on close, which only works for a toggle.
- **One popup at a time**; a new `notify` replaces the current one. The notifier is a single
  resident process in its own `vetro-popup-notifications` systemd unit, supervised by the Shard.
- Snapshots are stills, fetched when the popup is shown — there is no live view by design.

## Layout

```text
popup-notifications/   the Shard as installed (this folder is what pack bundles)
  manifest.json
  index.js             listener, snapshot fetch, supervision, event relay, config routes
  ui.html              the dashboard page
  notify.runtimeconfig.json
  notify.dll.gz.b64    generated by scripts/build-native.sh
native/                the notifier source
  Program.cs           window, layout, drawing, keys, events, evdev supervision
  Efl.cs, Libc.cs      the P/Invoke surface (from the launcher)
  Evdev.cs             input_event decoding and remote-node discovery
  NavigationInput.cs   key codes → key names, and evas echo suppression
  Notify.csproj        netcoreapp3.1, like the launcher
blueprints/            Home Assistant blueprint
scripts/
  build-native.sh
docs/
  PROTOCOL.md
```

## Credits

The window technique, the input handling and the supervision pattern are Vetro's
([andrearicci93/vetro](https://github.com/andrearicci93/vetro)); the in-bundle native binary
follows its 8BitDo Guide Shard. Vetro is MIT.
