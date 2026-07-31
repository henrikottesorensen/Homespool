# Print host upload paths: OctoPrint, PrusaLink, and the two Prusa Connect flows

Findings from reading the PrusaSlicer sources — v2.9.6, commit `b028299c7`. This documents **every
HTTP request** PrusaSlicer issues on each of the four paths, what it expects back, how it
authenticates, and which config keys drive it.

All `src/...` paths and `file:line` references below are relative to the **PrusaSlicer** checkout,
not to this repository. Line numbers are valid as of that commit.

Related material in this folder: `prusa-link-openapi.yaml` (the server side of §3),
`prusa-connect-mobile-app.jsonopenapi`, `prusa-connect-cameras.md`.

Files involved:

| File | Role |
| --- | --- |
| `src/slic3r/Utils/PrintHost.{hpp,cpp}` | Abstract host interface, factory, background upload queue |
| `src/slic3r/Utils/OctoPrint.{hpp,cpp}` | `OctoPrint`, `PrusaLink`, `PrusaConnect` (legacy), `SL1Host` |
| `src/slic3r/Utils/PrusaConnect.{hpp,cpp}` | `PrusaConnectNew` (the "Send to Connect" button) |
| `src/slic3r/Utils/ServiceConfig.{hpp,cpp}` | Base URLs of all Prusa online services |
| `src/slic3r/Utils/Http.{hpp,cpp}` | libcurl wrapper; all HTTP goes through it |
| `src/slic3r/GUI/UserAccount*.{hpp,cpp}` | OAuth session, token refresh, Connect polling |
| `src/slic3r/GUI/PrintHostDialogs.cpp` | Send dialog + upload queue dialog / notifications |
| `src/slic3r/GUI/PhysicalPrinterDialog.cpp` | Host-type selection and its guard rails |
| `src/slic3r/GUI/WebViewDialog.cpp` | `PrinterPickWebViewDialog` for the new Connect flow |

Notation used throughout: `{host}` = the `print_host` config value after `make_url` normalisation,
`{connect}` / `{account}` / `{printables}` / `{media}` = the corresponding `ServiceConfig` base URLs.

---

## 1. Common infrastructure

### 1.1 Host selection

`PrintHost::get_print_host(DynamicPrintConfig*)`
(`src/slic3r/Utils/PrintHost.cpp:44`) instantiates a host from the `host_type`
enum of a *physical printer* config. For `ptSLA` presets the enum is ignored and `SL1Host` is used,
unless `host_type == htPrusaConnectNew`.

```
htOctoPrint       -> OctoPrint
htPrusaLink       -> PrusaLink
htPrusaConnect    -> PrusaConnect      (legacy, subclass of PrusaLink)
htPrusaConnectNew -> PrusaConnectNew   (Send to Connect)
htDuet/htFlashAir/htAstroBox/htRepetier/htMKS/htMoonraker -> respective classes
```

### 1.2 Transport defaults

Every request is a libcurl easy handle created by `Http::priv::priv`
(`src/slic3r/Utils/Http.cpp:165-186`):

- `CURLOPT_CONNECTTIMEOUT = 10` s; `CURLOPT_TIMEOUT = 0` — **no total-transfer timeout**
- `CURLOPT_USERAGENT = "PrusaSlicer/<version>"` — sent on every request below
- `CURLOPT_HTTP_VERSION = CURL_HTTP_VERSION_1_1` — forced, no HTTP/2
- `CURLOPT_FOLLOWLOCATION = 1` with `CURLOPT_POSTREDIR = CURL_REDIR_POST_ALL` — redirects are
  followed and **POST/PUT bodies plus auth headers are replayed to the redirect target**
- `CURLOPT_VERBOSE` when the log level is >= 5
- Verbs: `Http::get` plain, `Http::post` sets `CURLOPT_POST`, `Http::put` sets `CURLOPT_UPLOAD`
  with `CURLOPT_INFILESIZE` and streams from an `ifstream`
  (`src/slic3r/Utils/Http.cpp:723-740`)
- Nothing suppresses `Expect: 100-continue`, so libcurl adds it for bodies over 1 KiB — i.e. for
  effectively every upload on every path here

Retries: `perform_sync()` defaults to `HttpRetryOpt::no_retry()`. **Every print-host call site uses
that default, so an upload is never retried.** The exponential-backoff policy (500 ms initial,
capped at 256 s, 16 attempts) is used only by Prusa Account / session traffic
(`src/slic3r/Utils/Http.cpp:481`).

TLS: `printhost_cafile` → `CURLOPT_CAINFO`; `printhost_ssl_ignore_revoke` →
`CURLSSLOPT_REVOKE_BEST_EFFORT`, applied on Windows only. Neither reaches `PrusaConnectNew`.

### 1.3 Job queue and callbacks

`PrintHostJobQueue` runs a single detached background thread that pops jobs and calls
`printhost->upload(...)` synchronously, one at a time
(`src/slic3r/Utils/PrintHost.cpp:180-218`). The sliced temp file is
`fs::remove`d after each job regardless of outcome.

`info_fn(tag, status)` is a small string protocol consumed by `PrintHostQueueDialog::on_info`
(`src/slic3r/GUI/PrintHostDialogs.cpp:511`):

| Tag | Effect |
| --- | --- |
| `resolve` | Replaces the Host column / notification host with a resolved IP or URL |
| `set_complete_off` | Stops the notification auto-completing at 100 % upload |
| `complete` | Marks complete; `status` shown as the message |
| `complete_with_warning` | Same, flagged as a warning (used for HTTP 202) |
| `prusaconnect_printer_address` | Turns the notification into a link opening the Connect tab |

Error text: `PrintHost::format_error` yields `"HTTP <status>: <body>"` when a status code was
received, otherwise the raw curl error string (`src/slic3r/Utils/PrintHost.cpp:81`).

### 1.4 Which button appears

`Plater::priv::show_action_buttons` (`src/slic3r/GUI/Plater.cpp:4151`):
**Send G-code** when a physical printer with a non-empty `print_host` is selected; **Send to
Connect** only when no physical printer is selected *and* the printer preset's vendor id contains
`"Prusa"`. Mutually exclusive.

---

## 2. OctoPrint (`host_type = octoprint`)

Config consumed: `print_host`, `printhost_apikey`, `printhost_cafile`,
`printhost_ssl_ignore_revoke`.

### 2.1 URL construction

`OctoPrint::make_url` (`src/slic3r/Utils/OctoPrint.cpp:532`) prefixes
`http://` when `print_host` has no scheme and collapses a duplicate slash. **A bare hostname is
always plain HTTP** — there is no HTTPS upgrade or probing.

### 2.2 Authentication

`X-Api-Key: <printhost_apikey>` on every request (`src/slic3r/Utils/OctoPrint.cpp:523`).
No alternative scheme. The `print_host` tooltip additionally documents embedding HTTP basic
credentials in the URL (`https://user:pass@host/`) for hosts behind HAProxy — that is handled by
libcurl's userinfo parsing, not by any slicer code.

### 2.3 Complete request catalogue

| # | Trigger | Request |
| --- | --- | --- |
| O1 | Physical Printer dialog → **Test** button | `GET {host}/api/version` |
| O2 | `upload()` → `upload_inner_with_host` pre-flight | `GET {host}/api/version` |
| O3 | `upload()` → `upload_inner_with_host` | `POST {host}/api/files/local` |
| O4 | *(Windows)* `upload_inner_with_resolved_ip` pre-flight | `GET {ip-substituted host}/api/version` |
| O5 | *(Windows)* `upload_inner_with_resolved_ip` | `POST {ip-substituted host}/api/files/local` |

There is no other HTTP traffic on this path. Discovery uses mDNS, not HTTP (§2.5).

**O1 / O2 — version probe** (`src/slic3r/Utils/OctoPrint.cpp:234`)

```
GET {host}/api/version
X-Api-Key: <printhost_apikey>
```

Expected body: JSON containing an `api` member. If a `text` member is present it must start with
`"OctoPrint"`; a **missing `text` is accepted**
(`src/slic3r/Utils/OctoPrint.cpp:518`). Mismatch →
*"Mismatched type of print host: &lt;text&gt;"*. Unparseable JSON → *"Could not parse server
response"*. Any non-2xx aborts the upload before the file is sent.

O2 is unconditional: **every upload costs a version GET first**, so the minimum is two requests
(three on the Windows resolved-IP path).

**O3 — the upload** (`src/slic3r/Utils/OctoPrint.cpp:430`)

```
POST {host}/api/files/local
X-Api-Key: <printhost_apikey>
Content-Type: multipart/form-data; boundary=...
Expect: 100-continue
```

Parts, built with the legacy `curl_formadd` API (not the MIME API):

| Part | Value |
| --- | --- |
| `print` | `"true"` when the user chose *Upload and Print*, else `"false"` |
| `path` | parent directory of the target path, sent **unescaped** |
| `file` | the G-code, part filename = target filename |

The file part is streamed with `CURLFORM_STREAM` rather than `CURLFORM_FILECONTENT`, so Unicode
paths work on Windows (`src/slic3r/Utils/Http.cpp:286`). No explicit content type
is set on the part.

Response: any 2xx is success. The body is logged at debug level and otherwise **discarded** — no
`info_fn("complete", …)` is emitted, so the notification completes purely on reaching 100 % upload.

**O4 / O5 — Windows resolved-IP variants** (`src/slic3r/Utils/OctoPrint.cpp:175`,
`src/slic3r/Utils/OctoPrint.cpp:365`)

Identical to O2/O3 except the host in the URL is replaced by a literal IP via `substitute_host`, and
an explicit `Host: <original hostname>` header is re-added — libcurl would otherwise send the IP,
which breaks reverse proxies (issue #9734).

### 2.4 Windows mDNS workaround (non-HTTP)

On Windows only, `upload()` resolves the host itself first
(`src/slic3r/Utils/OctoPrint.cpp:302-362`):

- literal IP → used directly;
- `*.local` **and** app config `allow_ip_resolve` → Bonjour query for service `octoprint`,
  5 retry rounds, 1 s timeout;
- 0 results → fall back to system resolution (O2/O3);
- 1 result → O4/O5 against it;
- exactly one v4 + one v6 → try both in order, reporting both errors if both fail;
- more → `IPListDialog` asks the user.

Skipped entirely for `https://` hosts, since the certificate would not match the IP.

The **Browse** button uses the same mDNS service name, with TXT keys `version` and `model`
(`src/slic3r/GUI/BonjourDialog.cpp:130`). Note that PrusaLink and legacy
PrusaConnect inherit this code unchanged, so they too advertise/query as `octoprint`.

### 2.5 Capabilities

`get_post_upload_actions()` = `StartPrint` only. `has_auto_discovery()` = true.
`get_groups`/`get_printers`/`get_storage` fall through to base no-ops → no storage or group
selection in the send dialog.

---

## 3. PrusaLink (`host_type = prusalink`)

`PrusaLink : OctoPrint`. Adds `printhost_authorization_type`, `printhost_user`,
`printhost_password`.

### 3.1 Authentication

`PrusaLink::set_auth` (`src/slic3r/Utils/OctoPrint.cpp:595`) switches on
`printhost_authorization_type` (default `atKeyPassword`):

| Value | Effect |
| --- | --- |
| `key` (`atKeyPassword`) | `X-Api-Key: <printhost_apikey>` |
| `user` (`atUserPassword`) | `CURLAUTH_DIGEST` with `printhost_user` / `printhost_password` |

Digest auth means libcurl performs the challenge-response dance, so **each logical request below
becomes two round trips** (401 + authenticated retry). `printhost_cafile` is applied in both modes.

### 3.2 Complete request catalogue

| # | Trigger | Request |
| --- | --- | --- |
| L1 | Physical Printer dialog → **Test** button (`PrusaLink::test`) | `GET {host}/api/version` |
| L2 | `Plater::send_gcode`, before the send dialog opens | `GET {host}/api/v1/storage` |
| L3 | `upload_inner_with_host` → `test_with_method_check` | `GET {host}/api/version` |
| L4 | `put_inner` (when `upload-by-put`) | `PUT {host}/api/v1/files/{storage}/{path}` |
| L5 | `post_inner` (otherwise) | `POST {host}/api/files/{storage}` |
| L6 | *(Windows)* `test_with_resolved_ip_and_method_check` | `GET {ip-substituted host}/api/version` |
| L7 | *(Windows)* `upload_inner_with_resolved_ip` | L4 or L5 against the substituted host |

**L1 / L3 / L6 — version probe** (`src/slic3r/Utils/OctoPrint.cpp:639`,
`src/slic3r/Utils/OctoPrint.cpp:801`,
`src/slic3r/Utils/OctoPrint.cpp:873`)

Same shape as O1, but `PrusaLink::validate_version_text` accepts a `text` starting with either
`"PrusaLink"` or `"OctoPrint"` and **rejects a missing `text`**
(`src/slic3r/Utils/OctoPrint.cpp:590`).

L3 and L6 additionally walk the response for a `capabilities` subtree and read
`upload-by-put` (boolean) to choose the upload verb
(`src/slic3r/Utils/OctoPrint.cpp:841`). Absent ⇒ legacy POST. Expected body:

```json
{"api": "0.1", "server": "0.7.0", "text": "PrusaLink 0.7.0",
 "capabilities": {"upload-by-put": true}}
```

L1 (the Test button) does **not** do the capability check — it is the plain `test()` override.

**L2 — storage enumeration** (`src/slic3r/Utils/OctoPrint.cpp:694`)

```
GET {host}/api/v1/storage
X-Api-Key: <printhost_apikey>          (or digest)
Accept-Language: <first two chars of the UI language code>
```

Expected: the **first** ptree member must be named `storage_list`. Each entry may carry `name`,
`path`, `free_space` (a *string*, parsed with `stoll`), `read_only` — or `ro`, as emitted by
PrusaLink 0.7.0RC2 — and `available`. Entries are offered only when not read-only and
`free_space > 0`. Missing `free_space` is treated as "space available"; missing `read_only`/`ro` as
writable.

Error handling is deliberately asymmetric: a curl-level failure (`status == 0`) sets `res = true`
so the error surfaces, while an HTTP error status sets `res = false` and stays silent — older
firmware simply lacks the endpoint. If the probe succeeded but nothing usable came back, it throws
`Slic3r::IOError`, aborting the send with a dialog that lists why each storage was rejected.

**L4 — raw PUT** (`src/slic3r/Utils/OctoPrint.cpp:1040`)

```
PUT {host}/api/v1/files/{storage}/{percent-escaped path}
X-Api-Key: <printhost_apikey>          (or digest)
Content-Type: text/x.gcode
Overwrite: ?1
Print-After-Upload: ?1                 (only when the action is StartPrint)
Host: <original hostname>              (Windows resolved-IP path only)
Expect: 100-continue
Content-Length: <file size>
```

Body is the raw file, streamed. Each path element is escaped separately with `curl_easy_escape`, so
`/` separators survive (`src/slic3r/Utils/OctoPrint.cpp:151`). `Overwrite`
and `Print-After-Upload` are RFC 8941 structured-field booleans; the `?1` spelling is deliberate,
because PrusaLink used to accept *any* non-empty string as true, so `?0` would also have started a
print (`src/slic3r/Utils/OctoPrint.cpp:1058-1060`).

`info_fn("set_complete_off")` is emitted before the request, `info_fn("complete")` on success.

**L5 — multipart POST** (`src/slic3r/Utils/OctoPrint.cpp:1090`)

```
POST {host}/api/files/{storage}
X-Api-Key: <printhost_apikey>          (or digest)
Content-Type: multipart/form-data; boundary=...
```

Parts: `print` = `"true"`/`"false"` (from `PrusaLink::set_http_post_header_args`), `path`, `file` —
as for O3.

**Storage path composition** (shared by L4/L5): the base is `api/v1/files` or `api/files`, then
`"/local"` if no storage was chosen, otherwise the storage string **appended verbatim**. Values
from L2 already begin with `/` (e.g. `/usb`), so the result is `api/v1/files/usb`
(`src/slic3r/Utils/OctoPrint.cpp:1001`).

### 3.3 Behaviour notes

`get_post_upload_actions()` = `StartPrint`. Storage selection is offered.
`SL1Host` is the same class with `validate_version_text` requiring a `"Prusa SLA"` prefix and no
post-upload actions — its request catalogue is identical to PrusaLink's.

The PUT path never calls `set_http_post_header_args`, so any post-upload action other than
`StartPrint` is silently lost on modern firmware. Irrelevant for PrusaLink (which only offers
`StartPrint`); it matters for legacy Connect — see §4.5.

---

## 4. Legacy Prusa Connect (`host_type = prusaconnect`)

`PrusaConnect : PrusaLink`, constructed with `show_after_message = true`
(`src/slic3r/Utils/OctoPrint.hpp:111`,
`src/slic3r/Utils/OctoPrint.cpp:1155`).

### 4.1 Authentication — in detail

This is the part most easily misread, because the type is named after an online service but
authenticates like a LAN device.

**No Prusa Account involvement whatsoever.** There is no OAuth, no `Authorization: Bearer`, no token
refresh, and `ServiceConfig` is not consulted at request time. `PrusaConnect` inherits
`PrusaLink::set_auth` verbatim — it does not override it — so the credential is whatever the
physical printer config holds:

| Config key | Type | Role here |
| --- | --- | --- |
| `printhost_apikey` | string, label *"API Key / Password"* | Sent as `X-Api-Key` |
| `printhost_authorization_type` | enum `key` \| `user`, default `key` | Selects key vs. digest |
| `printhost_user` / `printhost_password` | string / password | Digest credentials, if selected |
| `printhost_cafile` | string | `CURLOPT_CAINFO` for the TLS handshake |
| `printhost_ssl_ignore_revoke` | bool, Windows only | `CURLSSLOPT_REVOKE_BEST_EFFORT` |

Four consequences worth knowing:

1. **The dialog hides the auth-type selector but does not reset it.**
   `PhysicalPrinterDialog::update()` takes the `else` branch for any host type that is not
   `htPrusaLink` and calls `hide_field("printhost_authorization_type")` plus
   `show_field("printhost_apikey", true)` (`src/slic3r/GUI/PhysicalPrinterDialog.cpp:645`).
   It never writes the option. So a physical printer that was previously PrusaLink with
   *HTTP digest* keeps `printhost_authorization_type = user` after being switched to PrusaConnect —
   the UI then shows an API-key box that `set_auth` ignores, and requests go out with digest
   credentials instead. The code comment *"PrusaConnect does NOT allow http digest"* describes an
   intent the config layer does not enforce.
2. **The API key travels in a header on every request, in cleartext if the URL has no scheme.**
   `make_url` prefixes `http://`, never `https://` (§2.1). The dialog pre-fills
   `https://connect.prusa3d.com`, so the default is safe, but a hand-edited host is not.
3. **Redirects replay the credential.** With `CURL_REDIR_POST_ALL` and `FOLLOWLOCATION`, a 30x
   response causes the `X-Api-Key` header and the file body to be re-sent to whatever host the
   `Location` names.
4. **Digest costs an extra round trip per request** — with four requests on a POST-mode upload
   (test + version + 401 + upload), that is noticeable.

The **Test** button is *not* hidden for this host type despite the source comment claiming otherwise
(`update()` only calls `m_printhost_browse_btn->Hide()`,
`src/slic3r/GUI/PhysicalPrinterDialog.cpp:653`), and
`can_test()` is inherited as `true`. Pressing it issues C1 below against whatever URL is in the
Host field.

### 4.2 Complete request catalogue

| # | Trigger | Request |
| --- | --- | --- |
| C1 | **Test** button (`PrusaLink::test`) | `GET {host}/api/version` |
| C2 | `upload_inner_with_host` → `test_with_method_check` | `GET {host}/api/version` |
| C3 | `put_inner` (when `upload-by-put`) | `PUT {host}/api/v1/files/local/{path}` |
| C4 | `post_inner` (otherwise) | `POST {host}/api/files/local` |
| C5 | *(Windows)* resolved-IP variants of C2–C4 | as L6/L7 |

That is the whole list. In particular **there is no storage request**: `get_storage()` is overridden
to `return false` without issuing anything (`src/slic3r/Utils/OctoPrint.hpp:120`),
so `upload_data.storage` is always empty and the path segment is always `/local`.

C1–C3 are byte-for-byte the PrusaLink requests (L1, L3, L4). Only C4 differs.

**C4 — multipart POST with the Connect-specific fields**
(`src/slic3r/Utils/OctoPrint.cpp:1160`)

```
POST {host}/api/files/local
X-Api-Key: <printhost_apikey>          (or digest)
Accept-Language: <first two chars of the UI language code>
Content-Type: multipart/form-data; boundary=...
```

| Part | When |
| --- | --- |
| `to_print` = `"True"` | action is `StartPrint` |
| `to_queue` = `"True"` | action is `QueuePrint` |
| `path` | always (parent directory) |
| `file` | always |

Note `"True"` with a capital T here, versus `"true"`/`"false"` for OctoPrint/PrusaLink's `print`
field. The two are mutually exclusive because `post_action` is a single enum value; when the action
is `None` (plain *Upload*), neither field is added.

### 4.3 Response handling

Because `m_show_after_message` is true, the POST response body is decoded as UTF-8 and shown to the
user in the queue dialog's message column and the notification, and **HTTP 202 is mapped to
`complete_with_warning`** instead of `complete`
(`src/slic3r/Utils/OctoPrint.cpp:1111-1128`). PrusaLink, with the flag
false, emits an empty `complete` instead. On the PUT path (C3) the body is shown but the 202
distinction is not made (`src/slic3r/Utils/OctoPrint.cpp:1064`).

### 4.4 GUI guard rails

`PhysicalPrinterDialog` (`src/slic3r/GUI/PhysicalPrinterDialog.cpp:652`):

1. The dropdown entry is offered only when *every* preset attached to the physical printer is a
   Prusa Research vendor preset passing `model_supports_prusa_service()` (excludes MK2, allows
   MK2.5+) (`src/slic3r/GUI/PhysicalPrinterDialog.cpp:762`).
2. Selecting it **overwrites** the Host field with `ServiceConfig::connect_url()`, stashing the
   previous value in `m_stored_host` for restoration if the user switches away.
3. Digest auth selector hidden (but not reset — §4.1).
4. Browse (mDNS) button hidden.
5. On OK, a URL differing from `connect_url()` triggers *"URL of Prusa Connect is different from
   %1%. Do you want to continue?"* — answering Yes keeps the custom URL
   (`src/slic3r/GUI/PhysicalPrinterDialog.cpp:903`).

`MainFrame::show_printer_webview_tab` skips the embedded printer web tab for this host type
(`src/slic3r/GUI/MainFrame.cpp:938`).

So the target *is* configurable — the dialog just pushes it back to `https://connect.prusa3d.com`.
Whether that host still serves a PrusaLink-shaped `/api/version` and `/api/files` is not
determinable from the source; the client code assumes it does.

### 4.5 The PUT/POST trap

`to_print` / `to_queue` are only sent on C4. If the server advertises
`capabilities.upload-by-put`, C3 runs instead and sends only `Print-After-Upload: ?1` for
`StartPrint` — **"Upload to Queue" then degrades to a plain upload with no error shown.**

---

## 5. New Prusa Connect — the "Send to Connect" button (`host_type = prusaconnectnew`)

Wired at `src/slic3r/GUI/Sidebar.cpp:662` → `Plater::connect_gcode()`
(`src/slic3r/GUI/Plater.cpp:6695`).

### 5.1 Base URL is not user-configurable

`PrusaConnectNew::get_host()` returns `ServiceConfig::instance().connect_url()`
(`src/slic3r/Utils/PrusaConnect.hpp:36`). The `print_host` field is reused
to carry the printer **UUID** and `printhost_apikey` the **team id**
(`src/slic3r/GUI/Plater.cpp:6680-6690`) — neither is a URL.

`ServiceConfig` defaults (`src/slic3r/Utils/ServiceConfig.cpp:44`):

| Service | Default | Env override |
| --- | --- | --- |
| Connect | `https://connect.prusa3d.com` | `PRUSA_CONNECT_URL` |
| Account | `https://account.prusa3d.com` | `PRUSA_ACCOUNT_URL` |
| Media | `https://media.printables.com` | `PRUSA_MEDIA_URL` |
| Preset repo | `https://preset-repo-api.prusa3d.com` | `PRUSA_PRESET_REPO_URL` (also `SLIC3R_REPO_URL` at build time) |
| Printables | `https://www.printables.com` | `PRUSA_PRINTABLES_URL` |
| OAuth client id | `oamhmhZez7opFosnwzElIgE2oGgI2iJORSkw587O` | `PRUSA_ACCOUNT_CLIENT_ID` (no whitelist) |

Env overrides are read **once**, at first use of the singleton, and are **domain-whitelisted**: the
apex domain (last two labels, via `Http::get_apex_domain`) must be one of `prusa.com`,
`prusa3d.com`, `prusa.cz`, `prusa3d.cz`, `printables.com`, `testprusaverse.com`, `localhost`. A
rejected value is logged as *"Url was not set from env variable … not whitelisted"* and the default
kept (`src/slic3r/Utils/ServiceConfig.cpp:32-61`). There is **no
AppConfig / PrusaSlicer.ini key** for any of them.

### 5.2 Authentication — OAuth 2 authorization code + PKCE

Bearer tokens from `UserAccountSession` (`src/slic3r/GUI/UserAccountSession.hpp:116`).
The access token is a JWT; its lifetime is taken by decoding the `exp` claim
(`Utils::get_exp_seconds`), **not** from the response's `expires_in`
(`src/slic3r/GUI/UserAccountSession.cpp:275`). Login fails unless
`access_token`, `refresh_token` and `shared_session_key` are all present and `exp` is in the future.

Unlike print-host traffic, all of §5.3 uses `HttpRetryOpt::default_retry()` and raises a
*"Communication with Prusa Account is taking longer than expected. Retrying. Attempt N."*
notification from attempt 2.

### 5.3 Account / session request catalogue

| # | Trigger | Request |
| --- | --- | --- |
| A1 | Login (external browser or embedded webview) | `GET {account}/o/authorize/?embed=1&client_id=…&response_type=code&code_challenge=…&code_challenge_method=S256&scope=basic_info&redirect_uri=prusaslicer://login&language=…` |
| A2 | Login via a named service | `GET {account}/login/{service}?next=/o/authorize/?<same params>` |
| A3 | `prusaslicer://login` callback | `POST {account}/o/token/` — code exchange |
| A4 | Token near expiry / 401 recovery | `POST {account}/o/token/` — refresh |
| A5 | After login, and as a token liveness test | `GET {account}/api/v1/me/` |
| A6 | Connect status | `GET {connect}/slicer/status` |
| A7 | Periodic polling when enabled | `GET {connect}/slicer/v1/printers` |
| A8 | Printer detail by UUID | `GET {connect}/app/printers/{uuid}` |
| A9 | Avatar (relative) | `GET {media}/media/{path}` — no auth header |
| A10 | Avatar (absolute URL) | `GET {url}` — no auth header |
| A11 | Printables handoff | `POST {printables}/auth/get-secret-token` |

A1/A2 are navigations, not `Http` calls — they open a browser or webview
(`src/slic3r/GUI/UserAccountCommunication.cpp:392`). The
`code_challenge` is base64(SHA-256(verifier)).

**A3 — code exchange** (`src/slic3r/GUI/UserAccountSession.cpp:100`)

```
POST {account}/o/token/
Content-type: application/x-www-form-urlencoded

code=<code>&client_id=<client_id>&grant_type=authorization_code
&redirect_uri=prusaslicer://login&code_verifier=<verifier>
```

**A4 — refresh** (`src/slic3r/GUI/UserAccountSession.cpp:350`)

```
POST {account}/o/token/
Content-type: application/x-www-form-urlencoded

grant_type=refresh_token&client_id=<client_id>&refresh_token=<refresh_token>
```

Both expect `{"access_token": …, "refresh_token": …, "shared_session_key": …}`. A failed refresh
clears the session and fires `EVT_UA_RESET` (logout).

**A5–A8** are `UserActionGetWithEvent` (`src/slic3r/GUI/UserAccountSession.cpp:83`):
plain GET with `Authorization: Bearer <access_token>`, URL = configured base + a caller-supplied
suffix (the UUID for A8). In debug builds the token's `exp` is verified locally before sending.

**A9/A10** are the same action type constructed with `requires_auth_token = false`, so no
`Authorization` header is attached.

**A11 — Printables secret token** (`src/slic3r/GUI/UserAccountCommunication.cpp:538`)

```
POST {printables}/auth/get-secret-token
Content-type: application/json
Origin: {printables}

{"accessToken": "<access_token>"}
```

Note the access token is in the **body**, and the custom headers replace the default
`application/x-www-form-urlencoded` — `UserActionPost` sets the default content type only when
`additional_headers` is empty (`src/slic3r/GUI/UserAccountSession.cpp:55`).

`ServiceConfig::account_logout_url()` (`{account}/logout`) is declared but has **no callers**.

### 5.4 WebView navigations

| # | Trigger | URL |
| --- | --- | --- |
| W1 | "Send to Connect" → `PrinterPickWebViewDialog` | `{connect}/slicer-select-printer` |
| W2 | Printables → print | `{connect}/slicer-print?url=<escaped download url>` |
| W3 | Connect tab in the main frame | `{connect}/app/printers/` etc. |

W1 (`src/slic3r/GUI/WebViewDialog.cpp:455`) is the printer picker. It is
a full web app, not a REST call, with a `_prusaSlicer` script message handler.

Slicer → page, once the page signals ready:

```js
window._prusaConnect_v2.requestCompatiblePrinter({
  "printerUuid": "...", "printerModel": "MK4S",
  "nozzle_diameter": [0.4], "material": ["PLA"],
  "filename": "...", "filament_abrasive": [false],
  "high_flow": [false], "multiple_beds": false
})
```

Page → slicer: a JSON message whose `filename`, `team_id` and `data` members are extracted
(`src/slic3r/GUI/Plater.cpp:6666-6690`). Missing any of the three aborts with
*"Failed to read response from Prusa Connect server. Upload is cancelled."*

The `data` subtree is **passed through verbatim as the body of N1 below** — the slicer does not
construct it. Documented fields: `set_ready`, `position` (`0` = front of queue, `-1` = back),
`wait_until` (timestamp for deferred print), `file_name`, `printer_uuid`. This is where queue
placement lives in the new flow; `PrintHostSendDialog` is never shown, so
`get_post_upload_actions()` (which does declare `StartPrint | QueuePrint`) is effectively unused and
`upload_data.post_action` only reaches a log line.

For multi-bed autoslicing, `Plater::connect_gcode_all` reuses one picker result to build one job per
sliceable bed (`src/slic3r/GUI/Plater.cpp:6702`) — i.e. N1+N2 repeat per bed.

### 5.5 Upload request catalogue

| # | Trigger | Request |
| --- | --- | --- |
| N1 | `PrusaConnectNew::init_upload` | `POST {connect}/app/users/teams/{team_id}/uploads` |
| N2 | `PrusaConnectNew::upload` | `PUT {connect}/app/teams/{team_id}/files/raw?upload_id={id}` |
| N3 | `PrusaConnectNew::test` — **unreachable in the current UI** | `GET {connect}/app/teams/{team_id}/files?printer_uuid={uuid}` |
| N4 | `PrusaConnectNew::get_storage` — **unreachable in the current UI** | `GET {connect}/app/printers/{uuid}/storages` |

**N1 — register the upload** (`src/slic3r/Utils/PrusaConnect.cpp:79`)

```
POST {connect}/app/users/teams/{team_id}/uploads
Authorization: Bearer <access_token>
Content-Type: application/json

<data_json, with %1% -> upload filename and %2% -> file size in bytes>
```

Both placeholders are `assert`ed to be present in the webview-supplied JSON. Expected reply, of
which **only `id` is read**:

```json
{"id": 1234, "team_id": 12345, "name": "f.gcode", "size": 123, "hash": "...",
 "state": "INITIATED", "source": "CONNECT_USER", "path": "/usb/f.bgcode"}
```

Missing `id` or unparseable JSON → *"Failed to extract upload id from server reply."* On HTTP error
the body's `message` member is preferred as the user-facing error, falling back to `format_error`
(`src/slic3r/Utils/PrusaConnect.cpp:110`).

**N2 — the payload** (`src/slic3r/Utils/PrusaConnect.cpp:155`)

```
PUT {connect}/app/teams/{team_id}/files/raw?upload_id={id}
Authorization: Bearer <access_token>
Content-Type: text/x.gcode
Expect: 100-continue
Content-Length: <file size>

<raw file>
```

Response body is logged only. Before N1, `info_fn("prusaconnect_printer_address",
"{connect}/printer/{uuid}/dashboard")` makes the upload notification clickable.

**N3 / N4 — implemented but never reached.** The button routes through
`Plater::priv::export_gcode` → `background_process.schedule_upload`
(`src/slic3r/GUI/Plater.cpp:2714`), which enqueues the job directly; only
`Plater::send_gcode` calls `get_storage` and opens `PrintHostSendDialog`. For reference, N4 expects
a `storages` array keyed by `mountpoint` — a different schema from PrusaLink's
`storage_list`/`path` — with `name`, `free_space`, `read_only`/`ro`, `available`
(`src/slic3r/Utils/PrusaConnect.cpp:200`). N3 carries the Bearer header
and is described in its own comment as unused by upload.

---

## 6. Side-by-side

| | OctoPrint | PrusaLink | PrusaConnect (legacy) | PrusaConnectNew |
| --- | --- | --- | --- | --- |
| Requests per upload (best case) | 2 | 3 (version, storage, upload) | 2 | 2 |
| Target URL | `print_host` | `print_host` | `print_host` (dialog forces Connect URL, overridable with a warning) | `ServiceConfig::connect_url()`, env-overridable within a whitelist |
| Credential | `X-Api-Key` | `X-Api-Key` or HTTP digest | `X-Api-Key` or HTTP digest (selector hidden, value not reset) | `Authorization: Bearer` (OAuth 2 + PKCE) |
| Pre-flight | `GET api/version` | `GET api/version` (+ `upload-by-put`), `GET api/v1/storage` | `GET api/version` (+ `upload-by-put`) | none |
| Upload verb | POST multipart | PUT raw, or POST multipart | PUT raw, or POST multipart | POST JSON, then PUT raw |
| Upload path | `api/files/local` | `api/v1/files/{storage}/{path}` | `api/v1/files/local/{path}` | `app/users/teams/{team}/uploads`, then `app/teams/{team}/files/raw?upload_id=` |
| Start print | form `print=true` / header `Print-After-Upload: ?1` | same | form `to_print=True` (POST only) | inside the opaque `data` JSON |
| Queue print | – | – | form `to_queue=True` (POST only) | `position` / `wait_until` in `data` |
| Storage choice | no | yes | no | no |
| Server message shown | no | no | yes; 202 ⇒ warning | error `message` only |
| Auto-discovery | mDNS `octoprint` | mDNS `octoprint` | hidden (code still inherits it) | n/a |
| Retries | none | none | none | none for N1/N2; account traffic retries |

---

## 7. Observations worth keeping in mind

1. **Bare hostnames are plain HTTP.** `make_url` never upgrades to HTTPS, so `X-Api-Key` travels in
   cleartext unless the user typed `https://`.
2. **Redirects replay bodies and credentials.** `CURL_REDIR_POST_ALL` + `FOLLOWLOCATION` means a 30x
   from an untrusted host re-sends the file and the `X-Api-Key` / `Authorization` header to the
   redirect target.
3. **No transfer timeout.** `CURLOPT_TIMEOUT = 0`; a stalled connection is only broken by the user
   cancelling from the queue dialog.
4. **No retries on uploads**, in contrast to account traffic.
5. **Legacy Connect can silently use digest auth** — the dialog hides the selector without resetting
   the stored `printhost_authorization_type` (§4.1).
6. **`to_queue` is dropped on PUT-capable servers** (§4.5).
7. **`Content-Type: text/x.gcode` is hardcoded** on both raw-PUT paths, including binary G-code.
8. **`get_storage` parsing is positional** — it checks `ptree.front().first`, so a server emitting
   any other member first fails the whole parse. The two implementations also disagree on schema
   (`storage_list`/`path` vs `storages`/`mountpoint`).
9. **Env overrides are read once per process** and whitelisted to Prusa domains plus `localhost`;
   the apex check takes the last two labels, so `connect.example.com` is rejected while any
   `*.prusa3d.com` passes.
10. **`PrusaConnectNew::test()` / `get_storage()` and `account_logout_url()` are dead code** in the
    current flows.
11. **The Test button is live for legacy Connect** despite a source comment claiming it is hidden,
    and it issues `GET {host}/api/version` against the Connect URL.
