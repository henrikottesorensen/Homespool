#!/usr/bin/env bash
#
# Move an enrolled printer into a current database, so that regenerating the migration does not mean
# walking to the machine.
#
# WHY THIS EXISTS. AGENT-NOTES section 2 regenerates the single migration in place while this project
# is pre-release, and says to delete the database afterwards. That is cheap until the database has a
# printer enrolled in it, because re-enrolling is the one thing that cannot be done from a keyboard:
# both channels need somebody at the printer, reading a code off its screen or carrying a USB key to
# it. On 2026-08-18 that was somebody 25 km away.
#
# WHAT THE PRINTER ACTUALLY NEEDS. It holds its own fingerprint and token in flash and presents them
# on every request, so what keeps it enrolled is one row in PrusaConnectAuthentication pointing at
# one row in Printers. Nothing else about the old database matters to it.
#
# THE APPROACH, AND WHY IT IS NOT A WHOLE-DATABASE COPY. Let the new database be created and set up
# normally - first run, admin bootstrap, its default team - and then adopt the old printer into it.
# That way every column the current code expects is written by the current code, including ones that
# did not exist when the old database was made. Copying a membership row forward cannot do that: it
# would have to invent a value for TeamMember.Capabilities, which is precisely the kind of guess that
# produces an account that exists and can do nothing.
#
# Usage:
#   ./tools/carry-enrolment.sh check <old.sqlite> <new.sqlite>
#   ./tools/carry-enrolment.sh adopt <old.sqlite> <new.sqlite> [--team <id>] [--with-cameras]
#
# There is deliberately NO "upgrade the old database in place" subcommand. It was written first and
# then deleted, because the appliance it was written for turned out to be two migrations behind
# rather than one: adding the newest table and stamping the history row would have told EF the schema
# was current while three columns from the capability work were still missing. `check` is what is
# left of it - it reports the gap instead of papering over it.

set -euo pipefail

require_sqlite() {
    if ! command -v sqlite3 >/dev/null 2>&1; then
        echo "carry-enrolment: sqlite3 is not installed. On the appliance:" >&2
        echo "    sudo apt-get install -y sqlite3" >&2
        exit 1
    fi
}

exists() {
    [ -f "$1" ] || { echo "carry-enrolment: no such database: $1" >&2; exit 1; }
}

# Refuses a database something else has open for writing. A -wal file beside it is normal and is not
# evidence of a writer; failing to take an immediate transaction is.
refuse_if_busy() {
    if ! sqlite3 "$1" "BEGIN IMMEDIATE; ROLLBACK;" 2>/dev/null; then
        echo "carry-enrolment: $1 is locked. Stop Homespool first:  docker compose stop homespool" >&2
        exit 1
    fi
}

columns() {
    sqlite3 "$1" "PRAGMA table_info(\"$2\");" | cut -d'|' -f2 | sort
}

tables() {
    sqlite3 "$1" "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"
}

# The columns a table has in BOTH databases. Everything else in the new one keeps its default, which
# is how a column added since the old database was made gets a sensible value rather than a guess.
common_columns() {
    comm -12 <(columns "$1" "$3") <(columns "$2" "$3")
}

do_check() {
    local old=${1:-} new=${2:-}
    [ -n "$old" ] && [ -n "$new" ] || { echo "usage: carry-enrolment.sh check <old.sqlite> <new.sqlite>" >&2; exit 2; }
    exists "$old"; exists "$new"; require_sqlite

    echo "old: $old  ($(sqlite3 "$old" 'SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;' | tr '\n' ' '))"
    echo "new: $new  ($(sqlite3 "$new" 'SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;' | tr '\n' ' '))"
    echo

    local gap=0

    while read -r table; do
        if ! sqlite3 "$old" "SELECT 1 FROM sqlite_master WHERE type='table' AND name='$table';" | grep -q 1; then
            echo "  table missing from old: $table"
            gap=1
            continue
        fi

        local missing
        missing=$(comm -13 <(columns "$old" "$table") <(columns "$new" "$table") | tr '\n' ' ')

        if [ -n "${missing// /}" ]; then
            echo "  $table is missing: $missing"
            gap=1
        fi
    done < <(tables "$new")

    if [ "$gap" -eq 0 ]; then
        echo "  the old database has every table and column the new one does."
    else
        echo
        echo "  The old database is behind by more than a stamp. Do not copy its schema forward -"
        echo "  use 'adopt' to bring the printer into a database the current code created."
    fi
}

do_adopt() {
    local old=${1:-} new=${2:-}
    shift 2 || true

    local team="" cameras=0
    while [ $# -gt 0 ]; do
        case "$1" in
            --team) team=${2:-}; shift 2 ;;
            --with-cameras) cameras=1; shift ;;
            *) echo "carry-enrolment: unknown option $1" >&2; exit 2 ;;
        esac
    done

    [ -n "$old" ] && [ -n "$new" ] || { echo "usage: carry-enrolment.sh adopt <old.sqlite> <new.sqlite> [--team <id>] [--with-cameras]" >&2; exit 2; }
    exists "$old"; exists "$new"; require_sqlite
    refuse_if_busy "$new"

    # The team the printer lands in. Taken from the new database rather than the old one, because the
    # old team's row cannot be carried across without also carrying a membership, and a membership is
    # what this refuses to invent.
    if [ -z "$team" ]; then
        team=$(sqlite3 "$new" 'SELECT Id FROM Teams ORDER BY Id LIMIT 1;')
    fi

    if [ -z "$team" ]; then
        echo "carry-enrolment: $new has no teams yet. Finish first-run setup - create the administrator" >&2
        echo "  account - so there is a team for the printer to belong to, then run this again." >&2
        exit 1
    fi

    if [ "$(sqlite3 "$new" "SELECT COUNT(*) FROM Printers;")" != "0" ]; then
        echo "carry-enrolment: $new already has printers. Refusing rather than merging into it -" >&2
        echo "  ids are carried verbatim so the auth rows keep pointing at the right printer." >&2
        exit 1
    fi

    local printer_cols auth_cols
    printer_cols=$(common_columns "$old" "$new" Printers | grep -v '^TeamId$')
    auth_cols=$(common_columns "$old" "$new" PrusaConnectAuthentication)

    local printer_list printer_select
    printer_list=$(echo "$printer_cols" | sed 's/^/"/;s/$/"/' | paste -sd,)
    printer_select=$printer_list

    echo "Adopting the printer from $old into $new (team $team)"
    sqlite3 "$new" ".backup '$new.before-carry-enrolment'"
    echo "  backup: $new.before-carry-enrolment"

    sqlite3 "$new" <<SQL
PRAGMA foreign_keys = ON;
ATTACH DATABASE '$old' AS old;
BEGIN;

INSERT INTO "Printers" ($printer_list, "TeamId")
SELECT $printer_select, $team FROM old."Printers";

INSERT INTO "PrusaConnectAuthentication" ($(echo "$auth_cols" | sed 's/^/"/;s/$/"/' | paste -sd,))
SELECT $(echo "$auth_cols" | sed 's/^/"/;s/$/"/' | paste -sd,) FROM old."PrusaConnectAuthentication";

COMMIT;
DETACH DATABASE old;
SQL

    if [ "$cameras" -eq 1 ]; then
        local camera_cols camera_list
        camera_cols=$(common_columns "$old" "$new" Cameras | grep -v '^TeamId$')
        camera_list=$(echo "$camera_cols" | sed 's/^/"/;s/$/"/' | paste -sd,)

        sqlite3 "$new" <<SQL
PRAGMA foreign_keys = ON;
ATTACH DATABASE '$old' AS old;
BEGIN;
INSERT INTO "Cameras" ($camera_list, "TeamId") SELECT $camera_list, $team FROM old."Cameras";
COMMIT;
DETACH DATABASE old;
SQL
    fi

    echo "  printers:  $(sqlite3 "$new" 'SELECT COUNT(*) FROM Printers;')"
    echo "  auth rows: $(sqlite3 "$new" 'SELECT COUNT(*) FROM PrusaConnectAuthentication;')"
    [ "$cameras" -eq 1 ] && echo "  cameras:   $(sqlite3 "$new" 'SELECT COUNT(*) FROM Cameras;')"
    echo "  foreign keys: $(sqlite3 "$new" 'PRAGMA foreign_key_check;' | wc -l) violation(s)"
    echo "  integrity: $(sqlite3 "$new" 'PRAGMA integrity_check;')"
    echo
    echo "  The printer keeps its uuid, fingerprint and token, so it authenticates on its next"
    echo "  connection with nothing done at the machine. Print history and files are NOT carried."
}

case "${1:-}" in
    check) shift; do_check "$@" ;;
    adopt) shift; do_adopt "$@" ;;
    *)
        echo "usage: carry-enrolment.sh check <old.sqlite> <new.sqlite>" >&2
        echo "       carry-enrolment.sh adopt <old.sqlite> <new.sqlite> [--team <id>] [--with-cameras]" >&2
        exit 2
        ;;
esac
