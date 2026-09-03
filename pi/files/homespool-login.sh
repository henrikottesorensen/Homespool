#!/bin/sh
# Gives the device user a way to log in - a password, an SSH key, or both - from
# /boot/firmware/homespool-login.txt.
#
# The image ships with that account LOCKED and no authorised key, so nobody can log in, and this is
# how that changes. What it replaced was a stock password baked into every card: "homespool",
# published in the README, expired so the first login had to change it. That is the OctoPi
# arrangement and it does not survive contact with a network. Nothing here configures sshd, so it is
# Debian's default of PasswordAuthentication yes with UsePAM yes - and under PAM an expired password
# still *authenticates* over SSH and then asks for a new one. So from the moment a card reached a
# LAN until its owner first logged in, anyone who could reach port 22 could log in with the
# published credential and complete the change themselves. Expiry did not gate that; it handed the
# account over and locked the owner out.
#
# The boot partition is the only place a user can put anything before the board has ever run - it is
# the one partition a desktop machine can see, since the root filesystem is ext4, which Windows and
# macOS cannot mount at all. Same route as homespool-wifi, for the same reason.
#
# BOTH, not one or the other, and the console is what settles it. An SSH key does nothing at a
# monitor and keyboard, and a board whose networking is broken is exactly when the console is all
# there is - so a key-only card would offer a login prompt nobody can satisfy, which is the lockout
# this whole arrangement exists to prevent. A password covers the console; a key is the better
# credential for the network. They answer different failures, so the file takes either or both.
#
# THE GATES, one per credential, each closing when its own credential exists:
#
#   password   works while `passwd -S` reports L or NP; dead once it reports P
#   sshkey     works while authorized_keys is absent or empty; dead once it has a line in it
#
# So this can supply a first credential and never override one, whether that one came from here,
# from build.sh --password or --ssh-key, or from somebody typing passwd. Those are properties of the
# account itself rather than a stamp file recording what we think happened, so there is nothing that
# can drift out of step with reality.
#
# Deliberately not "has the user ever logged in", which is the same intent through a worse signal:
# trixie dropped the lastlog binary for a lastlog2 database, so that check would rest on a file
# format that has just churned, to learn what these two already say.
#
# The gates being independent is what makes a card provisioned key-only keep its password hatch, and
# that is the console recourse rather than an oversight. Filling in both lines shuts both, which is
# the thing to do on a card you care about.
set -eu

# One prefix in front of every path, so tests/pi-login.test.sh can point the whole set at a
# temporary tree. Empty in every real invocation: the unit passes no environment at all, and
# anything able to set it here is already root on the box. The alternative was a test that seds this
# file into a copy of itself, which would be testing something other than what ships - and this is a
# script whose mistakes leave somebody with a board in a cupboard and no way into it.
ROOT="${HOMESPOOL_LOGIN_ROOT:-}"

CONF="$ROOT/boot/firmware/homespool-login.txt"
DEFAULTS="$ROOT/etc/default/homespool-login"

# Blanks a credential where it sits. For the password that is the point - FAT32 carries no
# permissions, so the file is readable by anyone who ever puts the card in a machine, and it is the
# same mitigation homespool-wifi applies to the passphrase. A public key is not a secret and needs
# none of that; it is blanked for uniformity, so that the steady state of a provisioned card is an
# empty file this script passes over in silence, and so that one rule covers both lines. What was
# installed is on the board, in authorized_keys.
#
# What this is NOT is an erase, and the user-facing file says so rather than implying otherwise. sed
# -i rewrites, so the old bytes may remain in the partition's free space - and going further would
# not help: an SD card's controller does its own wear levelling, so no write from up here can be
# aimed at the cells holding the previous contents. Overwriting in place would look thorough and
# guarantee nothing. What blanking honestly buys is that the credential is no longer *in the file*,
# which is where anyone who mounts the card would look.
blank_line() {
    [ -f "$CONF" ] || return 0

    sed -i "s/^[[:space:]]*$1=.*/$1=/" "$CONF" || true
    sync
}

# Every value given for a key, and deliberately not `. "$CONF"`. Sourcing would execute whatever is
# in a file that any machine with an SD slot can write, and would break on a password containing a
# space or a dollar sign.
#
# tr -d '\r' is not cosmetic, and it is the trap both lines share. This file is edited on whatever
# desktop the user has, on a FAT32 partition - the most likely place in this entire image to acquire
# CRLF line endings. On the password a trailing carriage return becomes part of it: set, invisibly,
# to something the user cannot type. On a key it corrupts the base64 and sshd rejects the line
# without comment. Both fail as "the thing I typed simply does not work", which is the least
# debuggable outcome available on a board with no other way in.
#
# Everything after the first '=' is the value, so a password containing '=' survives intact - and so
# does the trailing '=' padding on a key.
configured() {
    [ -f "$CONF" ] || return 0

    sed -n "s/^[[:space:]]*$1=//p" "$CONF" | tr -d '\r'
}

# A key that is not one installs nothing and says nothing: sshd skips a line it cannot parse without
# comment, so the user is left with a card that "did not work" and no thread to pull. Checked by
# prefix rather than parsed - ssh-* covers rsa/ed25519/dss, ecdsa-* the NIST curves, sk-* the FIDO
# variants of both. Anything else is a typo, or a *private* key pasted by mistake, and saying which
# is the whole value of the check.
looks_like_public_keys() {
    printf '%s\n' "$1" | while IFS= read -r line; do
        [ -n "$line" ] || continue

        case "$line" in
            ssh-*|ecdsa-*|sk-ssh-*|sk-ecdsa-*) ;;
            *)
                echo "homespool-login: that does not look like a public key: ${line%% *}" >&2
                echo "homespool-login: it wants the contents of a .pub file, one per sshkey= line." >&2
                exit 1
                ;;
        esac
    done
}

# P, NP or L - a usable password, an empty one, or a locked account. Only P is a way in, so only P
# closes the gate; NP means the field is empty, which is a way in of the worst kind and worth
# replacing with a real password rather than protecting.
password_state() {
    passwd -S "$1" 2>/dev/null | awk '{ print $2 }'
}

# Whose credentials. Written at build time from the image's device user, because this script has no
# other way to know: the account is named by an rpi-image-gen build variable, and neither "pi" nor
# uid 1000 is a fact a script should be asserting on its own when the layer knows the answer.
#
# Sourced, unlike the file on the boot partition, and the difference is who can write it: this one
# is root-owned on the ext4 root filesystem, which is the ordinary /etc/default contract.
HOMESPOOL_USER=""

if [ -f "$DEFAULTS" ]; then
    . "$DEFAULTS"
fi

# One password, however many keys. A second password= line is a mistake and taking the first is the
# only sane reading of it; a second sshkey= line is somebody's other laptop, which is ordinary.
password=$(configured password | head -1)
sshkey=$(configured sshkey)

if [ -z "$password" ] && [ -z "$sshkey" ]; then
    # The steady state of every card, provisioned or not. Silent.
    exit 0
fi

if [ -z "$HOMESPOOL_USER" ]; then
    echo "homespool-login: no device user configured; leaving this card's credentials alone" >&2
    exit 0
fi

# The account's home, asked of the passwd database rather than assumed to be /home/<user>. Under
# test the prefix puts it inside the sandbox; in a real boot ROOT is empty and this is the path
# itself.
home="$ROOT$(getent passwd "$HOMESPOOL_USER" 2>/dev/null | cut -d: -f6)"

if [ "$home" = "$ROOT" ]; then
    echo "homespool-login: cannot find '$HOMESPOOL_USER'; doing nothing" >&2
    exit 1
fi

# ----------------------------------------------------------------------------------------------
# The password
# ----------------------------------------------------------------------------------------------
if [ -n "$password" ]; then
    state=$(password_state "$HOMESPOOL_USER")

    if [ -z "$state" ]; then
        echo "homespool-login: cannot read the password state of '$HOMESPOOL_USER'; doing nothing" >&2
        exit 1
    fi

    if [ "$state" = "P" ]; then
        # A password left in the file is still cleared, and loudly: the alternative is a plaintext
        # credential sitting on a FAT32 partition for the life of the card, which is the thing the
        # blanking exists to prevent.
        echo "homespool-login: IGNORED password - $HOMESPOOL_USER already has one, and this file" >&2
        echo "homespool-login: only sets the first. It has been cleared; log in and use passwd to" >&2
        echo "homespool-login: change the account's password instead." >&2
        blank_line password
    else
        # chpasswd rather than `usermod -p`: it hashes with whatever crypt method the system is
        # configured for - yescrypt on trixie - where usermod -p takes a hash and would make this
        # script the place that chooses one. It splits on the FIRST colon, so a password containing
        # colons is set correctly, and it writes the password field whole, so the locked account
        # this image ships with is unlocked by the same stroke.
        printf '%s:%s\n' "$HOMESPOOL_USER" "$password" | chpasswd

        # No `chage` here, and none at build time either any more. Expiry existed to blunt a
        # password everybody knew, and there is no longer one: every password this account can have
        # was chosen by its owner, so forcing a change at first login would ask for nothing and cost
        # an SSH session.
        echo "homespool-login: password set for $HOMESPOOL_USER"
        blank_line password
    fi
fi

# ----------------------------------------------------------------------------------------------
# The key
# ----------------------------------------------------------------------------------------------
if [ -n "$sshkey" ]; then
    keys="$home/.ssh/authorized_keys"

    if [ -s "$keys" ]; then
        echo "homespool-login: IGNORED sshkey - $HOMESPOOL_USER already has an authorised key, and" >&2
        echo "homespool-login: this file only installs the first. It has been cleared; append to" >&2
        echo "homespool-login: ~/.ssh/authorized_keys on the board instead." >&2
        blank_line sshkey
    elif ! looks_like_public_keys "$sshkey"; then
        # Left in the file rather than blanked: nothing was installed, so there is still something
        # to correct, and the line the user can see is what lets them correct it.
        exit 1
    else
        # 700 and 600, or sshd ignores the file outright - another silent failure. Written under a
        # umask rather than chmod-ed afterwards, so the key is never briefly world-readable.
        (
            umask 077
            mkdir -p "$home/.ssh"
            printf '%s\n' "$sshkey" > "$keys"
        )
        chmod 700 "$home/.ssh"
        chmod 600 "$keys"
        chown -R "$HOMESPOOL_USER:$HOMESPOOL_USER" "$home/.ssh"

        echo "homespool-login: authorised key installed for $HOMESPOOL_USER"
        blank_line sshkey
    fi
fi
