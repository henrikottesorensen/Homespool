#!/usr/bin/env python3
"""Creates the first administrator if needed, signs in, and claims a registration code.

Only here so that `rig/sdk-rig.py` can run unattended: claiming is a person's job in normal use, and
this does that person's part. It drives the real pages with a real cookie and real antiforgery
tokens, so it goes the same way a browser would rather than reaching into the database.

    ./rig/sdk-claim.py http://localhost:8080 <setup-token> <claim-code>

The setup token is printed once at startup by AdminBootstrap, held in memory only and regenerated on
every restart, so take it from the log of the run you are talking to. It is ignored when an
administrator already exists, which is why a second run signs in instead.

`rig/enrol.sh` does this same dance in shell for the firmware rig, and mints an API token besides.
This is the smaller Python half, kept separate because the SDK rig needs a claim and nothing else.

The account is the server's administrator, so its password is not written in this file. It is
PASSWORD if set, otherwise rig/password, which the first run generates and `rig/enrol.sh` shares.
The server must be a loopback address, or this would hand a real server an administrator whose
password crosses the network over plain HTTP. RIG_ALLOW_REMOTE=1 lifts the check.
"""
import ipaddress
import os
import re
import secrets
import sys
import urllib.parse
from pathlib import Path

import requests

BASE = sys.argv[1].rstrip("/")
SETUP_TOKEN = sys.argv[2]
CODE = sys.argv[3]

EMAIL = "sdk@example.com"
USERNAME = "sdkadmin"
PASSWORD_FILE = Path(os.environ.get("PASSWORD_FILE", Path(__file__).resolve().parent / "password"))


def is_loopback(url):
    # A URL with no scheme has no hostname to parse and is refused rather than guessed at.
    host = urllib.parse.urlsplit(url).hostname or ""
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return host == "localhost"


def rig_password():
    if os.environ.get("PASSWORD"):
        return os.environ["PASSWORD"]

    if not PASSWORD_FILE.exists():
        # Identity's composition rules are still on, so a draw is kept only if it has an upper, a
        # lower, a digit and a symbol. O_EXCL and 0600 at creation, so the file is never briefly
        # readable and never overwritten.
        while True:
            password = secrets.token_urlsafe(24)
            if all(re.search(c, password) for c in ("[A-Z]", "[a-z]", "[0-9]", "[-_]")):
                break
        fd = os.open(PASSWORD_FILE, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, "w") as f:
            f.write(password + "\n")
        print(f"== generated a password for {USERNAME} in {PASSWORD_FILE} ==")

    password = PASSWORD_FILE.read_text().strip()
    if not password:
        raise SystemExit(f"{PASSWORD_FILE} is empty - delete it to generate a new one, or set PASSWORD.")
    return password


if os.environ.get("RIG_ALLOW_REMOTE") != "1" and not is_loopback(BASE):
    raise SystemExit(f"{BASE} is not a loopback address. Set RIG_ALLOW_REMOTE=1 to claim against it anyway.")

PASSWORD = rig_password()

session = requests.Session()


def antiforgery(html):
    match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html)
    if not match:
        raise SystemExit("no antiforgery token in page")
    return match.group(1)


def post(path, data):
    page = session.get(BASE + path)
    data["__RequestVerificationToken"] = antiforgery(page.text)
    return session.post(BASE + path, data=data, allow_redirects=True)


# Setup only works once. On a second run the account already exists, so sign in instead - and
# either way what matters afterwards is holding a signed-in cookie.
setup_page = session.get(BASE + "/Setup")

if "Input.Token" in setup_page.text:
    print("== create the administrator ==")
    res = post("/Setup", {
        "Input.Email": EMAIL,
        "Input.Username": USERNAME,
        "Input.Password": PASSWORD,
        "Input.ConfirmPassword": PASSWORD,
        "Input.Token": SETUP_TOKEN,
    })
    print(f"  /Setup -> HTTP {res.status_code}, landed on {res.url}")
else:
    print("== sign in (an administrator already exists) ==")
    res = post("/Account/Login", {
        "Input.Login": USERNAME,
        "Input.Password": PASSWORD,
        "Input.RememberMe": "false",
    })
    print(f"  /Account/Login -> HTTP {res.status_code}, landed on {res.url}")
print("== claim the code ==")
page = session.get(BASE + "/Printers/Claim")
if "Input.Code" not in page.text:
    print(f"  cannot reach the claim page (HTTP {page.status_code}) - not signed in?")
    print("  an existing administrator only signs in with the password it was created with")
    raise SystemExit(1)

team = re.search(r'name="Input\.TeamId"[^>]*>.*?<option[^>]*value="(\d+)"', page.text, re.S)
data = {
    "Input.Code": CODE,
    "Input.Name": "SDK shim printer",
    "__RequestVerificationToken": antiforgery(page.text),
}
if team:
    data["Input.TeamId"] = team.group(1)

res = session.post(BASE + "/Printers/Claim", data=data, allow_redirects=True)
print(f"  /Printers/Claim -> HTTP {res.status_code}, landed on {res.url}")

body = res.text
for pattern in (r'alert-success[^>]*>\s*([^<]{5,200})', r'alert-danger[^>]*>\s*([^<]{5,200})'):
    found = re.search(pattern, body)
    if found:
        print(f"  page says: {found.group(1).strip()}")
