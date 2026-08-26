#!/usr/bin/env bash
#
# Move an enrolled printer into a current database, or bring an old database forward, so that
# regenerating the migration does not mean walking to the machine.
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
# THE THREE SUBCOMMANDS, AND THE ONE RULE THEY SHARE.
#
#   check    compares and reports. Changes nothing.
#   upgrade  compares, and if every difference is additively safe, applies it and stamps the history
#            in one transaction. Refuses anything else and says exactly what it found.
#   adopt    lets the new database be created and set up by the current code, then moves the old
#            printer and its credential into it. For the case no stamp can fix.
#
# THE RULE IS COMPARE, THEN STAMP - and what it forbids is stamping without comparing. A stamp is a
# claim about a schema, and stamping blind is how that claim becomes false.
#
# THIS IS NOT ADVICE, IT IS A SCAR. An earlier version of this script had an "upgrade the old
# database in place" subcommand that added the newest table and rewrote the history row WITHOUT
# comparing. It was written, tested and worked - against a database exactly one regeneration behind.
# The appliance it was written for turned out to be two behind, so it would have told EF the schema
# was current while six columns were missing, and the application would have failed later, on a
# column, with nothing left pointing at the cause. It was deleted rather than fixed, leaving `check`
# as the half that was honest.
#
# `upgrade` below is that subcommand written the other way round: the comparison is not a step it
# takes first, it is the thing that decides whether there is anything to apply at all. There is one
# comparison engine (build_plan), `check` and `upgrade` both read its output, and `upgrade` re-runs
# it afterwards and requires it to come back empty - so the tool checks its own claim instead of
# asserting it.
#
# WHAT ADOPT DELIBERATELY WILL NOT DO is carry a membership forward. That would mean inventing a
# value for TeamMember.Capabilities on a row that predates the column, and an account that exists and
# can do nothing is worse than one that does not. Letting first-run setup write it is the point.
#
# THE REFERENCE DATABASE. check and upgrade compare two databases rather than a database against a
# model, because on the appliance there is no other way - it has docker and no .NET SDK. The image
# that will not start is the one thing that definitively knows the schema it expects, so ask it:
#
#   docker compose run --rm --no-deps homespool --write-schema /app/data/reference.sqlite
#
# or, on a machine with the SDK:
#
#   dotnet run --project Homespool.Host -- --write-schema /tmp/reference.sqlite
#
# An image that PREDATES that argument does not recognise it, starts the server and writes nothing -
# the same trap --version documents, and confirmed on a real deployment. Check that the file appeared
# before trusting anything below it.
#
# WHERE TO RUN THIS. On an appliance the database is inside a root-owned docker volume, so this needs
# a container. Run it in the HOMESPOOL image rather than a throwaway alpine one - the image carries
# sqlite3 for exactly this, and running as the app's own user means nothing is left behind that the
# app cannot read. The database survives either way, being modified in place; what does not is the
# backup taken below, which an alpine-as-root run leaves owned by root, and a -wal from an
# interrupted run, which the app then cannot recover - reporting it as "could not enable write-ahead
# logging", the same sentence an unrelated permissions problem produces:
#
#   docker compose stop homespool
#   docker compose run --rm --no-deps -v ./carry-enrolment.sh:/ce.sh:ro --entrypoint bash homespool \
#       /ce.sh check /app/data/Homespool.Sqlite /app/data/reference.sqlite
#
# This script is deliberately NOT in the image - it is bind-mounted by that command. So a fixed
# version of it can be run against an image that predates the fix, which is the direction the fixes
# actually travel.
#
# Usage:
#   ./tools/carry-enrolment.sh check   <old.sqlite> <reference.sqlite>
#   ./tools/carry-enrolment.sh upgrade <old.sqlite> <reference.sqlite> [--dry-run]
#   ./tools/carry-enrolment.sh adopt   <old.sqlite> <new.sqlite> [--team <id>] [--with-cameras]

set -euo pipefail

# Every comparison below is comm over sorted files, and comm and sort must agree about what sorted
# means. They do not under a locale whose collation ignores punctuation, and column names are full of
# it - so the byte order is pinned rather than inherited from whoever is running this.
export LC_ALL=C

work=""

cleanup() {
    [ -n "$work" ] && rm -rf "$work"
    return 0
}

trap cleanup EXIT

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

stamp_of() {
    sqlite3 "$1" "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;" 2>/dev/null | tr '\n' ' '
}

# The columns a table has in BOTH databases. Everything else in the new one keeps its default, which
# is how a column added since the old database was made gets a sensible value rather than a guess.
common_columns() {
    comm -12 <(columns "$1" "$3") <(columns "$2" "$3")
}

# name|type|notnull|default|pk for every column of a table, sorted by name so two of these can be
# compared with comm. PRAGMA table_info's own order is declaration order, which differs between two
# databases for exactly the reason we are here.
column_facts() {
    sqlite3 "$1" "PRAGMA table_info(\"$2\");" \
        | awk -F'|' '{ print $2 "|" $3 "|" $4 "|" $5 "|" $6 }' \
        | sort
}

# The line of a CREATE TABLE that declares one column, verbatim from the database that has it.
#
# Verbatim rather than rebuilt from PRAGMA table_info, which knows the type, the nullability and the
# default and loses COLLATE, CHECK and REFERENCES - so a rebuilt definition is right until the day it
# quietly is not. EF writes one column per line, so the line is findable; when it is not findable
# unambiguously the caller blocks rather than guesses.
column_definition() {
    sqlite3 "$1" "SELECT sql FROM sqlite_master WHERE type='table' AND name='$2';" \
        | grep -E "^[[:space:]]*\"$3\"[[:space:]]" \
        | sed 's/^[[:space:]]*//; s/,[[:space:]]*$//'
}

# The table-level constraints of a CREATE TABLE - everything that is not a column declaration.
#
# These matter because ALTER TABLE ADD COLUMN cannot add one. A reference table carrying a foreign
# key or a unique constraint the old table lacks is therefore not additively reachable, however
# additive its columns look.
table_constraints() {
    sqlite3 "$1" "SELECT sql FROM sqlite_master WHERE type='table' AND name='$2';" \
        | { grep -E '^[[:space:]]*(CONSTRAINT|FOREIGN KEY|PRIMARY KEY|UNIQUE|CHECK)[[:space:]]' || true; } \
        | sed 's/^[[:space:]]*//; s/,[[:space:]]*$//; s/[[:space:]]\{1,\}/ /g' \
        | sort
}

# Indexes, views and triggers, as name and definition. Auto-indexes have a NULL sql and are excluded:
# they are SQLite's own consequence of a UNIQUE or PRIMARY KEY declaration, so they are already being
# compared as part of the table.
objects() {
    sqlite3 "$1" "SELECT type || ' ' || name || ' ' || replace(sql, char(10), ' ')
                  FROM sqlite_master
                  WHERE type IN ('index','view','trigger') AND sql IS NOT NULL
                  ORDER BY type, name;" \
        | sed 's/[[:space:]]\{1,\}/ /g' \
        | sort
}

# ------------------------------------------------------------------------------------------------
# The comparison engine
#
# One function, because two would drift, and the day they drifted would be the day a stamp made a
# claim the comparison had never checked. `check` reports what this produces and `upgrade` applies
# it; neither has an opinion of its own about what is safe.
#
# Writes four files into $3:
#   add-tables    a table the reference has and the old database does not
#   add-columns   "table|column" for a column that can be reached with ALTER TABLE ADD COLUMN
#   add-objects   "type name" for an index, view or trigger only the reference has
#   blocks        one sentence per difference that is not additively safe
#
# Everything not provably additive lands in blocks. That direction is deliberate: an unrecognised
# difference is a reason to stop, not a reason to continue.
# ------------------------------------------------------------------------------------------------
build_plan() {
    local old=$1 new=$2 out=$3

    : > "$out/add-tables"
    : > "$out/add-columns"
    : > "$out/add-objects"
    : > "$out/blocks"

    tables "$old" > "$out/old-tables"
    tables "$new" > "$out/new-tables"

    comm -13 "$out/old-tables" "$out/new-tables" > "$out/add-tables"

    while read -r table; do
        [ -n "$table" ] || continue
        echo "the old database has a table the reference does not: $table" >> "$out/blocks"
    done < <(comm -23 "$out/old-tables" "$out/new-tables")

    while read -r table; do
        [ -n "$table" ] || continue
        compare_table "$old" "$new" "$table" "$out"
    done < <(comm -12 "$out/old-tables" "$out/new-tables")

    objects "$old" > "$out/old-objects"
    objects "$new" > "$out/new-objects"

    # Compared by full definition, so a redefined index is neither silently kept nor silently
    # replaced: it appears as one addition and one removal, and the removal blocks.
    comm -13 "$out/old-objects" "$out/new-objects" | awk '{ print $1 " " $2 }' > "$out/add-objects"

    while read -r line; do
        [ -n "$line" ] || continue
        echo "the old database has $(echo "$line" | awk '{ print $1 " " $2 }') and the reference does not" \
            >> "$out/blocks"
    done < <(comm -23 "$out/old-objects" "$out/new-objects")
}

compare_table() {
    local old=$1 new=$2 table=$3 out=$4

    column_facts "$old" "$table" > "$out/old-cols"
    column_facts "$new" "$table" > "$out/new-cols"

    # Sorted again after the cut, not merely inherited from column_facts: "Id|INTEGER" sorts after
    # "IdName|TEXT" because '|' is above 'N', while "Id" sorts before "IdName". comm would silently
    # report both as one-sided.
    cut -d'|' -f1 "$out/old-cols" | sort > "$out/old-col-names"
    cut -d'|' -f1 "$out/new-cols" | sort > "$out/new-col-names"

    while read -r column; do
        [ -n "$column" ] || continue
        echo "the old database has a column the reference does not: $table.$column" >> "$out/blocks"
    done < <(comm -23 "$out/old-col-names" "$out/new-col-names")

    while read -r column; do
        [ -n "$column" ] || continue
        classify_new_column "$new" "$table" "$column" "$out"
    done < <(comm -13 "$out/old-col-names" "$out/new-col-names")

    # A column both have, declared differently. The type change is the case that matters and the one
    # no stamp can help with - integers already written into a column that is now TEXT do not become
    # names by being relabelled - but nullability and default are reported the same way, because
    # neither is reachable with ALTER TABLE ADD COLUMN either.
    while read -r column; do
        [ -n "$column" ] || continue

        local was is
        was=$(grep "^$column|" "$out/old-cols")
        is=$(grep "^$column|" "$out/new-cols")

        [ "$was" = "$is" ] && continue

        echo "$table.$column is declared $(describe_column "$was") here and $(describe_column "$is") in the reference" \
            >> "$out/blocks"
    done < <(comm -12 "$out/old-col-names" "$out/new-col-names")

    if ! diff -q <(table_constraints "$old" "$table") <(table_constraints "$new" "$table") >/dev/null; then
        echo "$table's table-level constraints differ from the reference's, and ALTER TABLE cannot add one" \
            >> "$out/blocks"
    fi
}

# Is a column the reference has and the old database lacks reachable with ALTER TABLE ADD COLUMN?
#
# SQLite's own restrictions do most of the deciding: it refuses a PRIMARY KEY or UNIQUE column, a
# NOT NULL with no default, a non-constant default, and a REFERENCES whose default is not NULL. Each
# of those is a real difference that this cannot apply, so each is a block naming itself rather than
# a failure at apply time.
classify_new_column() {
    local new=$1 table=$2 column=$3 out=$4

    local facts type notnull default pk definition
    facts=$(grep "^$column|" "$out/new-cols")
    type=$(echo "$facts" | cut -d'|' -f2)
    notnull=$(echo "$facts" | cut -d'|' -f3)
    default=$(echo "$facts" | cut -d'|' -f4)
    pk=$(echo "$facts" | cut -d'|' -f5)

    if [ "$pk" != "0" ]; then
        echo "$table.$column is new and part of the primary key, which ALTER TABLE cannot add" >> "$out/blocks"
        return
    fi

    if [ "$notnull" = "1" ] && [ -z "$default" ]; then
        echo "$table.$column is new, NOT NULL and has no default, so there is no value to give the existing rows" \
            >> "$out/blocks"
        return
    fi

    if [ -n "$default" ] && [ "${default#*(}" != "$default" ]; then
        echo "$table.$column is new and its default '$default' is an expression, which ALTER TABLE cannot add" \
            >> "$out/blocks"
        return
    fi

    definition=$(column_definition "$new" "$table" "$column")

    if [ "$(printf '%s\n' "$definition" | grep -c .)" != "1" ]; then
        echo "$table.$column is new but its definition could not be read unambiguously from the reference" \
            >> "$out/blocks"
        return
    fi

    case "$definition" in
        *UNIQUE*|*"PRIMARY KEY"*)
            echo "$table.$column is new and declares $type with a uniqueness constraint, which ALTER TABLE cannot add" \
                >> "$out/blocks"
            return
            ;;
    esac

    if [ "$notnull" = "1" ] && [ "${definition#*REFERENCES}" != "$definition" ]; then
        echo "$table.$column is new, NOT NULL and a foreign key, which ALTER TABLE can only add when it is nullable" \
            >> "$out/blocks"
        return
    fi

    echo "$table|$column" >> "$out/add-columns"
}

describe_column() {
    local type notnull default
    type=$(echo "$1" | cut -d'|' -f2)
    notnull=$(echo "$1" | cut -d'|' -f3)
    default=$(echo "$1" | cut -d'|' -f4)

    printf '%s %s' "${type:-(no type)}" "$([ "$notnull" = "1" ] && echo "NOT NULL" || echo "NULL")"
    [ -n "$default" ] && printf ' DEFAULT %s' "$default"
    return 0
}

plan_is_empty() {
    [ ! -s "$1/add-tables" ] && [ ! -s "$1/add-columns" ] && [ ! -s "$1/add-objects" ] && [ ! -s "$1/blocks" ]
}

report_plan() {
    local old=$1 new=$2 out=$3

    if [ -s "$out/add-tables" ] || [ -s "$out/add-columns" ] || [ -s "$out/add-objects" ]; then
        echo "  additive:"

        while read -r table; do
            [ -n "$table" ] && echo "    + table   $table"
        done < "$out/add-tables"

        while read -r entry; do
            [ -n "$entry" ] || continue
            local table column
            table=${entry%%|*}
            column=${entry#*|}
            printf '    + column  %-48s %s\n' "$table.$column" "$(describe_column "$(column_facts "$new" "$table" | grep "^$column|")")"
        done < "$out/add-columns"

        while read -r entry; do
            [ -n "$entry" ] && echo "    + $entry"
        done < "$out/add-objects"

        echo
    fi

    if [ -s "$out/blocks" ]; then
        echo "  NOT additive:"

        while read -r line; do
            [ -n "$line" ] && echo "    ! $line"
        done < "$out/blocks"

        echo
    fi
}

do_check() {
    local old=${1:-} new=${2:-}
    [ -n "$old" ] && [ -n "$new" ] || { echo "usage: carry-enrolment.sh check <old.sqlite> <reference.sqlite>" >&2; exit 2; }
    exists "$old"; exists "$new"; require_sqlite

    work=$(mktemp -d)

    echo "old:       $old  ($(stamp_of "$old"))"
    echo "reference: $new  ($(stamp_of "$new"))"
    echo

    build_plan "$old" "$new" "$work"
    report_plan "$old" "$new" "$work"

    if [ -s "$work/blocks" ]; then
        echo "  The old database differs from the reference in ways no stamp can fix. Do not stamp it -"
        echo "  use 'adopt' to bring the printer into a database the current code created."
        return 0
    fi

    if plan_is_empty "$work"; then
        if [ "$(stamp_of "$old")" = "$(stamp_of "$new")" ]; then
            echo "  Identical schema, identical stamp. Nothing to do."
        else
            echo "  Identical schema, different stamp - the migration was regenerated and nothing else"
            echo "  changed. 'upgrade' rewrites the history row and that is the whole fix."
        fi

        return 0
    fi

    echo "  Every difference is additively safe. 'upgrade' will apply them and stamp the history in"
    echo "  one transaction."
}

do_upgrade() {
    local old=${1:-} new=${2:-}
    shift 2 2>/dev/null || true

    local dry=0
    while [ $# -gt 0 ]; do
        case "$1" in
            --dry-run) dry=1; shift ;;
            *) echo "carry-enrolment: unknown option $1" >&2; exit 2 ;;
        esac
    done

    [ -n "$old" ] && [ -n "$new" ] || { echo "usage: carry-enrolment.sh upgrade <old.sqlite> <reference.sqlite> [--dry-run]" >&2; exit 2; }
    exists "$old"; exists "$new"; require_sqlite
    refuse_if_busy "$old"

    work=$(mktemp -d)

    echo "old:       $old  ($(stamp_of "$old"))"
    echo "reference: $new  ($(stamp_of "$new"))"
    echo

    build_plan "$old" "$new" "$work"
    report_plan "$old" "$new" "$work"

    # The gate. Not a check performed before the stamp - the thing that decides whether a stamp is
    # honest at all. Nothing below this line runs when the comparison found something it could not
    # classify as additive.
    if [ -s "$work/blocks" ]; then
        echo "  Refusing. Every line above marked ! is a difference this cannot apply, and stamping"
        echo "  the history would tell EF the schema is current while it is not - which is exactly"
        echo "  how the last database was broken."
        echo
        echo "  A type change in particular can never be stamped across: the rows already written do"
        echo "  not change shape because the column was relabelled. Carry the printer into a database"
        echo "  the current code created instead:"
        echo
        echo "    tools/carry-enrolment.sh adopt $old <a database this build created>"
        exit 1
    fi

    if plan_is_empty "$work" && [ "$(stamp_of "$old")" = "$(stamp_of "$new")" ]; then
        echo "  Identical schema, identical stamp. Nothing to do."
        return 0
    fi

    if [ "$dry" -eq 1 ]; then
        echo "  --dry-run: nothing was changed."
        return 0
    fi

    sqlite3 "$old" ".backup '$old.before-carry-enrolment'"
    echo "  backup: $old.before-carry-enrolment"

    write_upgrade_sql "$old" "$new" "$work" > "$work/upgrade.sql"

    # One transaction, and -bail so the first error stops the script before COMMIT is reached. The
    # connection then closes with the transaction open, which SQLite rolls back.
    if ! sqlite3 -bail "$old" < "$work/upgrade.sql"; then
        echo >&2
        echo "carry-enrolment: the upgrade failed and was rolled back - nothing in $old changed." >&2
        echo "  The statement sqlite3 refused is above. $old.before-carry-enrolment is a copy taken" >&2
        echo "  before this ran, if you would rather start from it." >&2
        exit 1
    fi

    # The tool checking its own claim. If the comparison and the application disagree - a definition
    # that did not mean what it looked like, a statement that silently did nothing - this is where it
    # surfaces, before anybody starts the application against it.
    build_plan "$old" "$new" "$work"

    if ! plan_is_empty "$work"; then
        echo >&2
        echo "carry-enrolment: the upgrade ran but the databases still differ. The stamp has been" >&2
        echo "  applied to a schema that does not match, which is the failure this tool exists to" >&2
        echo "  prevent. Restore $old.before-carry-enrolment and use 'adopt'." >&2
        echo >&2
        report_plan "$old" "$new" "$work" >&2
        exit 1
    fi

    echo "  stamped: $(stamp_of "$old")"
    echo "  foreign keys: $(sqlite3 "$old" 'PRAGMA foreign_key_check;' | wc -l | tr -d ' ') violation(s)"
    echo "  integrity: $(sqlite3 "$old" 'PRAGMA integrity_check;')"
    echo
    echo "  The schema now matches the reference and the history says so. Nothing was copied out of"
    echo "  the old database and nothing was deleted from it."
}

# One transaction covering the DDL and the stamp, in that order.
#
# ATTACH is outside it because SQLite will not attach inside a transaction. The history rows are
# copied from the reference rather than rebuilt from parsed text, so there is nothing to quote and
# nothing to escape: whatever the reference says is applied, including ProductVersion.
write_upgrade_sql() {
    local old=$1 new=$2 out=$3

    echo "ATTACH DATABASE '$new' AS ref;"
    echo "BEGIN IMMEDIATE;"

    while read -r table; do
        [ -n "$table" ] || continue
        sqlite3 "$new" "SELECT sql FROM sqlite_master WHERE type='table' AND name='$table';"
        echo ";"
    done < "$out/add-tables"

    while read -r entry; do
        [ -n "$entry" ] || continue
        local table column
        table=${entry%%|*}
        column=${entry#*|}
        echo "ALTER TABLE \"$table\" ADD COLUMN $(column_definition "$new" "$table" "$column");"
    done < "$out/add-columns"

    while read -r entry; do
        [ -n "$entry" ] || continue
        local kind name
        kind=$(echo "$entry" | awk '{ print $1 }')
        name=$(echo "$entry" | awk '{ print $2 }')
        sqlite3 "$new" "SELECT sql FROM sqlite_master WHERE type='$kind' AND name='$name';"
        echo ";"
    done < "$out/add-objects"

    echo "DELETE FROM \"__EFMigrationsHistory\";"
    echo "INSERT INTO \"__EFMigrationsHistory\" SELECT * FROM ref.\"__EFMigrationsHistory\";"
    echo "COMMIT;"
    echo "DETACH DATABASE ref;"
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
    upgrade) shift; do_upgrade "$@" ;;
    adopt) shift; do_adopt "$@" ;;
    *)
        echo "usage: carry-enrolment.sh check   <old.sqlite> <reference.sqlite>" >&2
        echo "       carry-enrolment.sh upgrade <old.sqlite> <reference.sqlite> [--dry-run]" >&2
        echo "       carry-enrolment.sh adopt   <old.sqlite> <new.sqlite> [--team <id>] [--with-cameras]" >&2
        exit 2
        ;;
esac
