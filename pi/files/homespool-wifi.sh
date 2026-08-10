#!/bin/sh
# Applies the Wi-Fi details a user typed into /boot/firmware/homespool-wifi.txt.
#
# The OctoPi arrangement, and for the same reason: the boot partition is the only one a desktop
# machine can see when the card is plugged in, so it is the only place a user can put anything
# before the board has ever run. The root filesystem is ext4, which Windows and macOS cannot mount
# at all.
#
# Note this only exists because the boot partition is not otherwise involved at runtime. The layer
# adds the fstab entry that mounts it; without that, /boot/firmware is a stale copy inside the root
# filesystem and edits made on the card would be read by nobody - which fails silently and looks
# exactly like a wrong password.
set -eu

CONF=/boot/firmware/homespool-wifi.txt
IWD_DIR=/var/lib/iwd

# Lift the rfkill soft block before anything else, and unconditionally - it is not related to whether
# a credential was configured, and a blocked radio is worth clearing even on an ethernet deployment.
#
# raspberrypi-sys-mods ships /etc/modprobe.d/rfkill_default.conf with `options rfkill
# default_state=0`, so wireless comes up **blocked**. We install that package for its root-resize
# scripts and inherit its wi-fi policy as a side effect, without any of the Raspberry Pi OS tooling
# that normally lifts the block once an operator has chosen a country.
#
# The symptom is thoroughly misleading: `iwd` logs "Error bringing interface up: Operation not
# possible due to RF-kill" once at boot, then `iwctl station list` reports no station at all, which
# reads as a driver or firmware problem rather than a policy switch.
#
# The regulatory reason for the block is real, so this does not ignore it: the kernel takes a
# regulatory domain from /etc/modprobe.d/cfg80211_regdomain.conf, and the country= line below sets
# iwd's too.
if command -v rfkill >/dev/null 2>&1; then
    rfkill unblock wifi || true
fi

[ -f "$CONF" ] || exit 0

# Deliberately not `. "$CONF"`. Sourcing would execute whatever is in a file that any machine with
# an SD slot can write, and would break on a passphrase containing a space or a dollar sign.
# tr -d '\r' is not cosmetic. This file is edited on whatever desktop the user has, on a FAT32
# partition - the most likely place in this entire image to acquire CRLF line endings. A trailing
# carriage return on the passphrase is invisible in every editor and makes iwd reject the credential
# exactly as though the password were wrong, which is the single least debuggable outcome available.
value_of() {
    sed -n "s/^[[:space:]]*$1=//p" "$CONF" | head -1 | tr -d '\r'
}

ssid=$(value_of ssid)
psk=$(value_of psk)
country=$(value_of country)

if [ -z "$ssid" ] || [ -z "$psk" ]; then
    exit 0
fi

# iwd.network(5): the SSID appears verbatim in the file name if it contains only alphanumerics,
# spaces, underscores or minus signs - otherwise the name is "=" followed by its lower-case hex.
# Getting this wrong does not error; iwd simply never matches the network to the credential.
case "$ssid" in
    *[!A-Za-z0-9\ _-]*)
        name="=$(printf '%s' "$ssid" | od -An -tx1 | tr -d ' \n')"
        ;;
    *)
        name="$ssid"
        ;;
esac

mkdir -p "$IWD_DIR"
chmod 700 "$IWD_DIR"

umask 077
printf '[Security]\nPassphrase=%s\n' "$psk" > "$IWD_DIR/$name.psk"
chmod 600 "$IWD_DIR/$name.psk"
echo "homespool-wifi: configured '$ssid'"

# iwd's own configuration, written whole rather than appended to - and it must stay whole, because
# an earlier version of this script wrote only the country here and silently discarded
# EnableNetworkConfiguration along with it.
#
# SaeDisable is the substantive line, and it is the conclusion of a long morning on real hardware.
# Measured on a Pi 4 (BCM43455, firmware 7.45.265, which does advertise extsae) against both a
# 2.4 GHz sae-mixed AP and a 5 GHz WPA3-only one:
#
#   - default iwd            no suitable BSS at all; it never transmits. wiphy_can_connect_sae()
#                            returns true because the driver advertises NL80211_FEATURE_SAE without
#                            auth/assoc commands, so iwd commits to SAE and rejects every BSS.
#   - DisablePMKSA=true      a genuine improvement - the SAE exchange completes, H2E and all - but
#                            the association is then rejected with status 16. Both bands.
#   - wpa_supplicant, SAE    identical: PMKSA cached, then ASSOC-REJECT status_code=16.
#   - wpa_supplicant default connects, because its default key_mgmt excludes SAE entirely.
#
# So SAE reaches association and is refused, whatever drives it. SaeDisable makes iwd take the
# WPA2 half of a mixed network, which is what wpa_supplicant does by default and what actually
# works. Its cost is stated plainly in iwd's own header: "This will prevent IWD from connecting to
# WPA3-only networks" - which on this silicon it could not do anyway.
#
# raspberrypi/linux#4718 has Raspberry Pi's own engineer saying the SAE_EXT work was done "using
# wpa_supplicant", and a reply the same day (2024-02-07) that it "just doesn't work with iwd".
# Revisit if that changes; the fix is deleting one line.
#
# Note the limit of what this buys, established by moving a working card from a Pi 4 into a Pi 3B:
# it is a Pi 4 remedy and it does not rescue a Pi 3B. Same image, same credential, same network,
# same 2.4 GHz channel - the Pi 3B refused association once a minute with `connect-failed,
# status: 16`, while joining a plain WPA2 network immediately. That network advertises
# `PSK PSK/SHA-256 SAE` with `MFP-capable`, and SAE was already disabled here, so SAE was not the
# obstacle: the BCM43430's 2021 firmware cannot complete association against a transition BSS at
# all, most likely over PSK/SHA-256 and PMF, neither of which a WPA2-only network asks for. No iwd
# setting helps - a Pi 3B needs a WPA2-only network or a cable, and the user-facing files say so.
#
# Worth carrying forward: `status: 16` appeared both with SAE (Pi 4) and without it (Pi 3B), so it
# means the AP stopped answering mid-handshake and nothing more specific. It is not an SAE marker.
mkdir -p /etc/iwd
{
    printf '[General]\n'
    printf 'EnableNetworkConfiguration=false\n'
    if [ -n "$country" ]; then
        printf 'Country=%s\n' "$country"
    fi
    printf '\n[DriverQuirks]\nSaeDisable=brcmfmac\n'
} > /etc/iwd/main.conf

# if, not `[ -n "$country" ] && echo ...`. That form is the last command in this block, so under
# `set -e` an empty country would exit non-zero - and homespool-firstboot.service's Restart=on-failure would
# then retry a script that had in fact done its job, forever.
if [ -n "$country" ]; then
    echo "homespool-wifi: regulatory country set to $country"
fi

# Blank the passphrase where it sits in the clear. FAT32 carries no permissions, so this file is
# readable by anyone who ever puts the card in a machine. The ssid stays, so the file still says
# which network the board was pointed at, and stays editable for changing networks later.
sed -i 's/^[[:space:]]*psk=.*/psk=/' "$CONF" || true
sync

# iwd reads this directory when it starts, and this unit is ordered before it - so on a first boot
# there is nothing to restart. try-restart covers the other case: a user who edited the file on a
# board that was already running and rebooted, where iwd may already be up.
if systemctl is-active --quiet iwd; then
    systemctl try-restart iwd || true
fi
