# Prusa Connect: camera protocol and the Prusa Wi-Fi Camera

A technical reference for the camera side of the Prusa Connect API, and for how the standalone
Prusa Wi-Fi Camera ("Buddy Camera") integrates with it.

## Scope and method

This document describes observed behaviour, gathered for interoperability purposes. Two independent
sources were used:

| Source | What it establishes |
|---|---|
| [`prusa3d/Prusa-Connect-SDK-Printer`](https://github.com/prusa3d/Prusa-Connect-SDK-Printer) | The published Python SDK. Authoritative for the *web camera* scheme, since it is a complete working client. |
| Official camera firmware image `cam-3.1.4` | The standalone Wi-Fi camera's OTA image. Establishes the *Wi-Fi camera* scheme, which the SDK does not cover. |

Firmware observations come from string and symbol analysis of the stripped application binary, not
from decompilation. Claims are marked **verified** (read directly from source or from unambiguous
binary evidence) or **inferred** (reasoned from naming, adjacency, or absence). Nothing here has
been confirmed against live network traffic, so this describes what the *clients* do, which is not
necessarily the full set of what Connect's servers accept.

Firmware version analysed: `cam-3.1.4`. Behaviour may differ in other releases.

---

## Overview: Connect has two camera schemes

Connect's UI offers two ways to add a camera, and they are genuinely different protocols that share
one data model.

| | **Web camera** | **Wi-Fi camera** |
|---|---|---|
| What it is | A camera attached to a host running the Connect SDK (e.g. PrusaLink on a Raspberry Pi with a USB or CSI camera) | The standalone Prusa Wi-Fi Camera, talking to Connect directly |
| Printer involved? | Yes — registers *through* the printer's connection | No |
| Registration | `POST /p/camera` | `POST /c/info` |
| Snapshot auth header | `Token` | `X-Camera-Token` |
| Provisioning | SDK configuration | QR code shown to the camera's own lens |
| Documented by Prusa | Yes, via the SDK | No |

**Endpoint namespaces.** `/p/` endpoints are printer-authenticated (`/p/register`, `/p/telemetry`,
`/p/ws`, `/p/camera`); `/c/` endpoints are camera-authenticated (`/c/info`, `/c/snapshot`).
*Inferred* from the observed set, but consistent across every endpoint seen.

---

## The Connect camera API

### Registration — web camera: `POST /p/camera`

**Verified** (SDK `models.py`, `__init__.py`).

Sent through the printer's own request queue, and therefore authenticated with the **printer's**
`Fingerprint` and `Token` headers. The body carries the camera's settings, its `camera_id`, and its
available resolutions.

On `200`, the response's **`Token` header is the camera's own token** — the printer vouches for the
camera, and Connect mints a camera credential in exchange.

### Registration — Wi-Fi camera: `POST /c/info`

**Verified** (firmware). Sent as `application/json` under the camera's own token. No printer is
involved at any point. Observed payload fields:

```
camera_id        model            firmware
manufacturer     trigger_scheme   available_resolutions
width            height
network_info { wifi_mac, wifi_ipv4, wifi_ssid }
```

`manufacturer` carries the value `Niceboy` — the ODM of the camera hardware.

This endpoint is re-sent to update camera info, not only at first registration.

### Snapshot upload — `PUT /c/snapshot`

**Verified** (both sources). The still-image ingest, used by both schemes.

```
PUT /c/snapshot?printer_uuid=<optional>
Timestamp:     <unix timestamp>
Fingerprint:   <the camera's fingerprint>
Content-Type:  image/jpg

<raw JPEG bytes>
```

Authentication differs by scheme, and this is the one place the two genuinely diverge:

- **Web camera (SDK):** `Token: <camera token>`
- **Wi-Fi camera (firmware):** `X-Camera-Token: <camera token>`

An implementation serving this endpoint should accept both spellings.

`printer_uuid` is an optional **query parameter** carrying the camera→printer association. Its
absence is what allows a camera to exist without being bound to a printer.

### The camera data model

**Verified** (SDK `const.py`), and corroborated by the firmware's `/c/info` payload using the same
vocabulary.

A camera is modelled as something you *trigger* to produce a JPEG. There is no streaming concept in
this model at all — live video (see RTSP/WebRTC below) is handled entirely outside it.

```python
class CapabilityType(Enum):
    TRIGGER_SCHEME = "trigger_scheme"  # Can trigger a camera
    IMAGING        = "imaging"         # Can get an image from a camera
    RESOLUTION     = "resolution"      # Can set a resolution
    ROTATION       = "rotation"        # Can rotate the image
    EXPOSURE       = "exposure"        # Can change exposure compensation
    FOCUS          = "focus"           # Can change the focal point
```

Trigger schemes: `TEN_SEC`, `THIRTY_SEC` (the default), `SIXTY_SEC`, `EACH_LAYER`, `FIFTH_LAYER`,
`MANUAL`. Only the three interval schemes have a fixed period; the layer-based schemes require
knowledge of print progress, which is why they are driven by a host that is running the print rather
than by a standalone camera.

### The SDK's driver model

**Verified** (SDK `camera_driver.py`). The web-camera scheme generalises to arbitrary hardware
through a plugin base class, `CameraDriver`:

| Member | Purpose |
|---|---|
| `scan()` / `_scan()` | Discovery — enumerate available cameras |
| `_connect()` | Open the device |
| `take_a_photo() -> bytes` | The one method a driver must implement |
| `set_resolution` / `set_rotation` / `set_exposure` / `set_focus` | Per-capability setters, defaulting to "not implemented" |
| `make_hash()` / `get_config_hash()` | Stable camera identity across restarts |

This is the supported route for third-party cameras: implement a driver, and the SDK handles
registration and upload.

### Printer firmware is not involved

**Verified.** Cameras never communicate through the printer's own Connect connection. Prusa's
printer firmware (checked at `v6.6.0`) contains no camera protocol code — the only matches for
"camera" in its source are an unrelated UI string in the registration dialog, LED-dimming settings,
and translation files. The printer and camera subsystems are disjoint and merely share a server.

---

## The Prusa Wi-Fi Camera

### Platform

**Verified** from the OTA image.

- **Rockchip camera SoC running embedded Linux.** Not a microcontroller.
- OTA image is a plain tar containing `boot.img` (FIT), `oem.img` (UBI filesystem), and an update
  script that writes partitions with `nandwrite` — i.e. raw NAND.
- **Nothing in the image is encrypted or signed-at-rest**; the filesystem extracts with standard UBI
  tooling.
- ODM is **Niceboy**; the vendor SDK ships largely intact, with Prusa's own application as a single
  binary (`lp_app`) alongside the stock Rockchip IPC sample suite.
- Prusa application components identifiable from RTTI: `PrusaRTSP`, `PrusaWebRTC`,
  `PrusaCommunicationService`, `PrusaTimelapsService`, `XhrQRRecognizeService`.

### Provisioning: the QR code channel

**Verified.** The camera reads QR codes with its own image sensor (it bundles the ZXing barcode
library). Connect's "Add Wi-Fi camera" dialog generates a QR client-side whose payload is plain
JSON:

```json
{"ssid":"<wifi ssid>","pwd":"<wifi password>","token":"<20-char token>"}
```

Notes on the payload:

- The token is **20 characters of base64url** — the same shape and length as a Connect printer
  token. It is minted by Connect *before* the camera exists and shipped directly, rather than being
  a short-lived pairing code exchanged for a credential.
- There is **no server host/address field** and no user reference. Server addresses are configured
  through a separate mechanism (below); the account association is held server-side against the
  token.
- The Wi-Fi SSID limit in the UI is 26 characters. The QR for short inputs is a version-3 symbol at
  ECC level M.

> **A generated QR contains your Wi-Fi password in plain text.** It is a credential in image form
> and should be treated like one.

### The QR is also a command channel

**Verified.** Beyond provisioning, the camera accepts QR codes carrying configuration commands. The
payload is parsed as JSON (nlohmann/json), unknown keys are rejected with a logged error, and each
recognised action produces distinct audio feedback.

| Action | Values |
|---|---|
| RTSP server mode | on / off |
| WebRTC mode | on / off |
| Video size | `sd`, `hd`, `fhd` |
| Light / IR control | `auto`, `night`, `day` |
| Volume | integer, range-checked |
| Wi-Fi + token | `ssid`, `pwd`, `token` |

Firmware-update and factory-reset paths also exist in this handler.

*Inferred, not confirmed:* the exact JSON key spellings for the command actions. The internal event
names and the accepted value strings are known; the key names are not fully established. The
provisioning keys (`ssid`, `pwd`, `token`) *are* confirmed, from a decoded QR.

The camera enters scan mode on a single button press.

### Button gestures

**Verified.**

```
single click    → start QR code scanning
double click    → change IR mode
10-second hold  → factory reset
```

### AT commands over serial

**Verified.** The firmware implements a genuine V.250-style AT command interpreter on a hardware
UART (`/dev/ttyS4`, configured through termios).

```
AT+WIFISSID=<ssid>     AT+WIFIPWD=<password>   AT+WIFICONNECT
AT+TOKEN=<token>       AT+RTSP=<0|1>           AT+WEBRTC=<0|1>
AT+IR=<1 auto|2 day|3 night>                   AT+STATUS
AT+VERSION?            AT+UPGRADE              AT+REBOOT
AT+CLAC
```

`AT+CLAC` returns the authoritative command list for a given firmware build.

This interface is gated on a runtime hardware check — the firmware logs whether the board supports
UART and skips the service if not — so it is not present on all units. Prusa's printer firmware
sends no AT commands, so the printer is not the intended host. *Inferred:* this is a factory
provisioning and development interface rather than a user-facing one.

### On-device configuration

**Verified.** Persisted at `/data/xhr_config.ini`, with credentials additionally mirrored into
`/data/xhr_http_token.conf` and `/data/xhr_http_fingerprint.conf`.

| Namespace | Keys |
|---|---|
| `http.` | `token`, `fingerprint` |
| `config.` | `camera_name`, `rtsp_server_mode`, `webrtc_mode`, `video_quality`, `snapshot_upload_interval`, `ir_mode`, `volume` |
| `wifi_ap.` | `ssid`, `pwd` — the camera's *own* access point, not the joined network |

The loader is tolerant: every key is optional, and missing or unparseable values fall back
per-field, with distinct handling for "not set" and "parse error". There is no server address in
this file.

**Values are encrypted at rest.** The configuration class implements its own encrypt/decrypt with
PKCS7 padding over OpenSSL's EVP interface — a block cipher, almost certainly AES-CBC. The key
source has not been established.

### Server endpoints, and how they are overridden

**Verified.** The camera talks to three separate services, each with a compiled-in default and each
overridable by a file on the camera's SD card.

| Service | Purpose | SD-card file | Format |
|---|---|---|---|
| Image server | `/c/info` registration and `/c/snapshot` uploads | `/mnt/sdcard/imgsrv` | `IMAGE_SRV=<url>` |
| Signalling server | socket.io control channel and WebRTC signalling | `/mnt/sdcard/siosrv` | line containing `URL=` (also `SIO_SRV=`, `SECLEVEL=`, `TYPE=`) |
| OTA server | firmware update manifests | `/mnt/sdcard/ota` | `OTA=<url>` |

Each has selectable variants — the image server has "stable"/"unstable" and the signalling server
"production"/"developer" forms, alongside staging and development hostnames in the binary. This
retargeting is a designed feature of the firmware, not a defect.

*Inferred:* the accepted value grammar (bare hostname vs. full URL, scheme and port handling) has
not been established beyond the key names.

### The socket.io control channel

**Verified.** The signalling server is not merely WebRTC transport — it is the camera's live command
channel, implemented over socket.io.

- **Authentication:** the camera sends an authentication message on connect and re-sends it on
  reconnection; a token change triggers a reconnect.
- **WebRTC signalling:** `offer`, `answer`, `candidate`, and a connection-info event. ICE is handled
  by libjuice, with protobuf-encoded payloads.
- **Remote commands received over this channel:** `get_snapshot`, `photo`,
  `enable_snapshot_upload`, `disable_snapshot_upload`, `reboot_device`.

That last group is significant: the same controls exposed locally via QR are also driven remotely
from the server side. This channel — not the image server — is how a camera is commanded in real
time.

### OTA updates

**Verified.** The camera fetches a JSON manifest containing `last_version`, `sha1sum`,
`last_release_type` and `force_upgrade`, then follows a host/path redirection to the image.

Offline updates are supported by placing a renamed update tarball on the SD card; the firmware
detects it at boot and reboots into recovery to apply it.

### RTSP and WebRTC modes

Recent firmware supports two live-video modes, **both Connect-enabled and mutually exclusive**:

- **RTSP** — the older mode. The stream is reachable on the local network.
- **WebRTC** — newer, with NAT traversal so the stream is viewable from outside the LAN.

*Reported behaviour:* upgrading firmware can switch a camera to WebRTC mode, which disables the
LAN-reachable RTSP stream. Either mode can be selected afterwards via QR code or AT command, and
the current mode persists in `config.rtsp_server_mode` / `config.webrtc_mode`.

### On-device timelapse

**Verified.** The firmware includes a timelapse service that writes to the SD card, independent of
any server-side timelapse feature.

### Diagnostics

**Verified.** The firmware can write a plain-text diagnostic report to `/mnt/sdcard/system_dump.txt`
containing: serial number, camera model, hardware and firmware versions, camera name, uptime,
timezone, CPU temperature, sensor calibration type/value, IR mode, Wi-Fi SSID/BSSID/IP/signal
quality, and MAC address.

### Recovery and development hooks

**Verified.** The boot script executes a shell script from the SD card in place of the main
application, if one is present, and supports flags on the SD card to suppress application start or
enable file logging.

This requires **physical access** to the device and is not remotely reachable. It appears to be a
development and recovery affordance rather than an intentional user feature.

---

## What is not established

- **Live traffic has not been observed.** Everything here describes client behaviour read from
  source and firmware, not a captured exchange with Connect's servers. The servers may accept more
  than these clients send.
- **Exact JSON key names for the QR command actions** (as distinct from provisioning, which is
  confirmed).
- **The encryption key source** for the on-device configuration.
- **The accepted value grammar** for the SD-card server-override files.
- **Whether the QR payload has conditional fields** beyond the three observed — only one QR, from
  one UI context, was decoded.
- **Server-side semantics of `printer_uuid`** — which identifier it carries and whether it is
  required for a camera to appear against a printer.
- **`/c/info` response handling** — the request is understood; what Connect returns is not.
