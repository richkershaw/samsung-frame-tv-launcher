# Popup notifications — protocol

Two interfaces: **a caller → the Shard** (a notification) and **the Shard → a webhook**
(an event). Both are plain HTTP/JSON. The Shard's own port is set on its page (default `8079`).
Nothing here is specific to Home Assistant; any program that can POST JSON and receive a
webhook works.

When a token is set, every request carries `?token=<shared token>` or the header `X-Popup-Token`.

## `POST /notify` — show a popup

| field | type | default | notes |
| --- | --- | --- | --- |
| `message` | string | — (required) | 1–400 chars. Wraps to the card width. |
| `title` | string | none | ≤ 60 chars, one line, bold. |
| `id` | string | none | ≤ 120 chars, echoed back on every event for this popup. |
| `timeout` | int seconds | config `defaultTimeout` (60) | `0` = stay until dismissed. 0–3600. |
| `pauseMedia` | bool | config `pauseMedia` (false) | Sends `pauseKey` before showing. |
| `sizeMultiplier` | number | 1.0 | 0.6–2.5. Scales the whole popup. |
| `image` | string | none | An `http(s)` URL (fetched by the TV) or an absolute path already on the TV. |
| `location` | string | config `defaultLocation` (`tr`) | `tl` `tr` `bl` `br` `center`. |
| `actions` | array | `[]` | Up to 4. See below. |

Each action:

| field | type | notes |
| --- | --- | --- |
| `label` | string | required, ≤ 24 chars, the button text. |
| `event` | string | required, `[A-Za-z0-9._:-]{1,64}`, sent to the webhook on press. |
| `context` | object | up to 20 keys, each `[A-Za-z0-9._-]{1,40}` with a string/number/bool value. |

**Dismiss is always the last button.** If the payload already contains an action whose
`event` equals the dismiss event name (config `dismissEvent`, default `dismiss`), that one is
used; otherwise the Shard appends one with the configured label.

Responses: `200 {"ok":true}`, `400 {"error":"…"}` (bad payload or image), `401` (token), `413` (body > 64 KB).

```sh
curl -sS -X POST http://192.168.1.50:8079/notify \
  -H 'content-type: application/json' \
  -d '{"title":"Front door","message":"Someone is at the front door",
       "timeout":45,"pauseMedia":true,"location":"tr",
       "image":"http://homeassistant.local:8123/api/camera_proxy/camera.front_door?token=…",
       "actions":[{"label":"Unlock","event":"front_door_unlock","context":{"lock":"front_door"}}]}'
```

## Events to the webhook

Every button press, and every dismissal (timeout, Back, Home, or the Dismiss button), is
POSTed to the configured **webhook URL** as:

```json
{
  "event": "front_door_unlock",
  "id": "front-1717",
  "label": "Unlock",
  "context": { "lock": "front_door" },
  "source": "popup-notifications",
  "at": "2026-11-15T10:00:00.000Z"
}
```

A dismissal uses the dismiss event name (`dismiss` by default) and the dismiss label, with an
empty context. The endpoint is any URL that accepts a JSON `POST`; the reply may be anything,
only a 2xx is read as success.

## Other endpoints

| | |
| --- | --- |
| `POST /dismiss` | Hide whatever is showing. `200 {"ok":true}`. |
| `GET /status` | `{ok, running, port, webhook, last, error}`. |
| `POST /event` | Internal: the notifier posts here; the Shard forwards to the webhook. Not for callers. |

## `job.json` — the Shard → notifier handoff (internal)

Written atomically in the plugin folder, then `notify` is pushed to the FIFO. The notifier
reads it on wake. The same fields as the payload, normalised, plus `scale`, `eventUrl`,
`eventToken`, `emitDismiss`, `dismissEvent`, and `context`/`dismiss` on each action.

## Limits

`message` 400 · `title` 60 · label 24 · id 120 · 4 actions + Dismiss · context 20 keys ·
payload 64 KB · image 5 MB · image types jpeg/png/gif/webp/bmp. Numbers out of range are
clamped, not refused; wrong types are refused.
