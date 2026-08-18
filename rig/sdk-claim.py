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
"""
import re
import sys

import requests

BASE = sys.argv[1].rstrip("/")
SETUP_TOKEN = sys.argv[2]
CODE = sys.argv[3]

EMAIL = "sdk@example.com"
USERNAME = "sdkadmin"
PASSWORD = "Correct-Horse-Battery-Staple-1!"

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
