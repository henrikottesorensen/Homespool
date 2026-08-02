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

if [ -n "$country" ]; then
    mkdir -p /etc/iwd
    printf '[General]\nCountry=%s\n' "$country" > /etc/iwd/main.conf
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
