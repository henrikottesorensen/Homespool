#!/usr/bin/env bash
#
# Bring a database from before files had a type up to the schema that has one, keeping every file,
# every copy on a printer and every queued print.
#
# WHAT CHANGED. PrintFiles became Files and gained a Type column; PrintFilesOnPrinters became
# FilesOnPrinters; both PrintFileId keys - there and on QueuedPrints - became FileId; and the unique
# index on a user's names now includes the type. carry-enrolment's upgrade refuses all of it, rightly:
# a renamed table, a renamed column and a NOT NULL column with no default are none of them additive,
# and a column with no value to give existing rows is exactly what it will not invent. Here the value
# is known - every row a database of that age holds is a print file - so this script is the step that
# supplies it, and carry-enrolment is still what checks the result and stamps it.
#
# HOW. The three tables are rebuilt rather than altered, because SQLite cannot add a NOT NULL column
# without a default, and a rename leaves the old names inside each table's own CREATE statement,
# which carry-enrolment compares verbatim. The new CREATE statements and indexes are read from the
# reference database rather than written here, so the script cannot drift from what the comparison
# expects. Every column of the old tables is carried across by name, PrintFileId as FileId; a column
# the new table has and the old one lacks, other than Type, stops the repair rather than taking a
# default.
#
# THE WHOLE PROCEDURE, on an appliance (the image carries sqlite3; see carry-enrolment's header for
# why it runs there and as the app's own user):
#
#   docker compose stop homespool
#   docker compose run --rm --no-deps homespool --write-schema /app/data/reference.sqlite
#   docker compose run --rm --no-deps -v ./repair-file-type.sh:/repair-file-type.sh:ro --entrypoint bash homespool \
#       /repair-file-type.sh /app/data/Homespool.Sqlite /app/data/reference.sqlite
#   docker compose run --rm --no-deps -v ./carry-enrolment.sh:/carry-enrolment.sh:ro --entrypoint bash homespool \
#       /carry-enrolment.sh upgrade /app/data/Homespool.Sqlite /app/data/reference.sqlite
#   docker compose start homespool
#
# The repair leaves a copy of the database beside it, <old>.before-file-type, and changes nothing
# unless every step succeeds: it runs in one transaction and checks the foreign keys before
# committing.
#
# Usage:
#   ./tools/repair-file-type.sh <old.sqlite> <reference.sqlite>

set -euo pipefail

export LC_ALL=C

if [ $# -ne 2 ]; then
    echo "usage: $0 <old.sqlite> <reference.sqlite>" >&2
    exit 2
fi

old=$1
ref=$2

if ! command -v sqlite3 >/dev/null 2>&1; then
    echo "repair-file-type: sqlite3 is not installed." >&2
    exit 1
fi

for db in "$old" "$ref"; do
    [ -f "$db" ] || { echo "repair-file-type: no such database: $db" >&2; exit 1; }
done

has_table() {
    [ "$(sqlite3 "$1" "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='$2';")" = "1" ]
}

columns() {
    sqlite3 "$1" "PRAGMA table_info(\"$2\");" | cut -d'|' -f2
}

# A path as an SQL string literal, for ATTACH.
sql_text() {
    local quote="'"
    printf "'%s'" "${1//$quote/$quote$quote}"
}

if has_table "$old" Files; then
    echo "repair-file-type: $old already has a Files table; there is nothing to repair." >&2
    exit 1
fi

for table in PrintFiles PrintFilesOnPrinters QueuedPrints; do
    has_table "$old" "$table" || { echo "repair-file-type: $old has no $table table, so it is not the schema this script repairs." >&2; exit 1; }
done

for table in Files FilesOnPrinters QueuedPrints; do
    has_table "$ref" "$table" || { echo "repair-file-type: $ref has no $table table; write it with the image you are upgrading to." >&2; exit 1; }
done

if ! sqlite3 "$old" "BEGIN IMMEDIATE; ROLLBACK;" 2>/dev/null; then
    echo "repair-file-type: $old is locked. Stop Homespool first:  docker compose stop homespool" >&2
    exit 1
fi

# The column lists for one copy: the new table's columns in order, each taken from the old table under
# its own name or, for FileId, as PrintFileId. Type is the one column supplied rather than carried.
copy_lists() {
    local old_table=$1 new_table=$2
    local into="" from="" column source

    while IFS= read -r column; do
        if [ "$column" = "Type" ] && [ "$new_table" = "Files" ]; then
            source="'GCode'"
        elif [ "$column" = "FileId" ]; then
            source='"PrintFileId"'
        elif columns "$old" "$old_table" | grep -qx "$column"; then
            source="\"$column\""
        else
            echo "repair-file-type: $new_table.$column has no counterpart in $old_table, and the repair will not invent one." >&2
            return 1
        fi

        into+="${into:+, }\"$column\""
        from+="${from:+, }$source"
    done < <(columns "$ref" "$new_table")

    printf '%s\n%s\n' "$into" "$from"
}

statement_for() {
    sqlite3 "$ref" "SELECT sql || ';' FROM sqlite_master WHERE type='$1' AND tbl_name='$2' AND sql IS NOT NULL ORDER BY name;"
}

script=$(mktemp)
trap 'rm -f "$script"' EXIT

{
    echo "PRAGMA foreign_keys = OFF;"
    echo "PRAGMA legacy_alter_table = ON;"
    echo "BEGIN;"
    echo "ALTER TABLE \"QueuedPrints\" RENAME TO \"QueuedPrints_before_file_type\";"
    echo "CREATE TEMP TABLE kept_sequence AS SELECT name, seq FROM sqlite_sequence WHERE name IN ('PrintFiles', 'PrintFilesOnPrinters', 'QueuedPrints_before_file_type');"

    for table in Files FilesOnPrinters QueuedPrints; do
        statement_for table "$table"
    done

    for pair in PrintFiles:Files PrintFilesOnPrinters:FilesOnPrinters QueuedPrints_before_file_type:QueuedPrints; do
        old_table=${pair%%:*}
        new_table=${pair##*:}
        source_table=$old_table

        # The renamed table is renamed only inside the transaction, so its columns are read under the
        # name it has outside it.
        [ "$old_table" = "QueuedPrints_before_file_type" ] && source_table=QueuedPrints

        lists=$(copy_lists "$source_table" "$new_table")
        into=$(sed -n 1p <<< "$lists")
        from=$(sed -n 2p <<< "$lists")

        echo "INSERT INTO \"$new_table\" ($into) SELECT $from FROM \"$old_table\";"
    done

    echo "DROP TABLE \"QueuedPrints_before_file_type\";"
    echo "DROP TABLE \"PrintFilesOnPrinters\";"
    echo "DROP TABLE \"PrintFiles\";"

    for table in Files FilesOnPrinters QueuedPrints; do
        statement_for index "$table"
    done

    # Ids handed out stay handed out: a deleted file's id is not reused for a new one.
    echo "UPDATE sqlite_sequence SET seq = max(seq, (SELECT seq FROM kept_sequence WHERE name = 'PrintFiles')) WHERE name = 'Files' AND EXISTS (SELECT 1 FROM kept_sequence WHERE name = 'PrintFiles');"
    echo "UPDATE sqlite_sequence SET seq = max(seq, (SELECT seq FROM kept_sequence WHERE name = 'PrintFilesOnPrinters')) WHERE name = 'FilesOnPrinters' AND EXISTS (SELECT 1 FROM kept_sequence WHERE name = 'PrintFilesOnPrinters');"
    echo "UPDATE sqlite_sequence SET seq = max(seq, (SELECT seq FROM kept_sequence WHERE name = 'QueuedPrints_before_file_type')) WHERE name = 'QueuedPrints' AND EXISTS (SELECT 1 FROM kept_sequence WHERE name = 'QueuedPrints_before_file_type');"
    echo "INSERT INTO sqlite_sequence (name, seq) SELECT 'Files', seq FROM kept_sequence WHERE name = 'PrintFiles' AND NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'Files');"
    echo "INSERT INTO sqlite_sequence (name, seq) SELECT 'FilesOnPrinters', seq FROM kept_sequence WHERE name = 'PrintFilesOnPrinters' AND NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'FilesOnPrinters');"
    echo "INSERT INTO sqlite_sequence (name, seq) SELECT 'QueuedPrints', seq FROM kept_sequence WHERE name = 'QueuedPrints_before_file_type' AND NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'QueuedPrints');"
    echo "DROP TABLE kept_sequence;"
} > "$script"

before=$(sqlite3 "$old" "SELECT (SELECT count(*) FROM PrintFiles) || ' ' || (SELECT count(*) FROM PrintFilesOnPrinters) || ' ' || (SELECT count(*) FROM QueuedPrints);")

# Everything into the main file first, so the copy below is the whole database: with the app stopped
# nothing else is reading, and a write-ahead log left beside it would otherwise hold the newest rows.
sqlite3 "$old" "PRAGMA wal_checkpoint(TRUNCATE);" >/dev/null
cp "$old" "$old.before-file-type"

# The foreign key check runs inside the transaction, and a violation aborts it: -bail ends the session
# at the first failing statement, which rolls the transaction back, and the CHECK below fails when the
# check finds anything.
if ! sqlite3 -bail "$old" <<SQL
$(cat "$script")
CREATE TEMP TABLE foreign_key_violations (count INTEGER CHECK (count = 0));
INSERT INTO foreign_key_violations SELECT count(*) FROM pragma_foreign_key_check;
COMMIT;
SQL
then
    echo "repair-file-type: the repair failed and was rolled back; $old is unchanged." >&2
    exit 1
fi

after=$(sqlite3 "$old" "SELECT (SELECT count(*) FROM Files) || ' ' || (SELECT count(*) FROM FilesOnPrinters) || ' ' || (SELECT count(*) FROM QueuedPrints);")

if [ "$before" != "$after" ]; then
    echo "repair-file-type: row counts moved from $before to $after; restore $old.before-file-type." >&2
    exit 1
fi

echo "repair-file-type: done - files, copies on printers and queued prints: $after."
echo "  The copy taken first is $old.before-file-type. Now run carry-enrolment's upgrade, which"
echo "  compares the result with the reference and stamps it."
