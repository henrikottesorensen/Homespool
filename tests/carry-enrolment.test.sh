#!/usr/bin/env bash
#
# Tests for tools/carry-enrolment.sh, and specifically for its refusals.
#
#   tests/carry-enrolment.test.sh              # run them all
#   tests/carry-enrolment.test.sh refuse       # run only tests whose name contains "refuse"
#
# No test framework, following tests/setup-env.test.sh for the same reason: this script exists so an
# operator with a broken appliance needs nothing but sqlite3, and a suite needing bats on every
# machine would undo half of that.
#
# WHAT IS ACTUALLY BEING TESTED. Not that `upgrade` can add a column - that is one ALTER TABLE. What
# earns the tests is everything it must REFUSE, because the subcommand this replaces was deleted for
# getting exactly that wrong: it worked, on the database it was written against, and would have
# stamped a false claim onto the one it met. So most of the cases below assert that nothing happened
# AND that the history row was left alone, since a refusal that still stamps is the failure mode.
#
# The schemas are synthetic and small. They are built to have one difference each, so a case that
# goes red names its own cause - a full EF schema has thirty tables and a failure in one of them
# reads as noise. The last case is the exception and uses the real thing: the schema this build
# actually produces, via --write-schema, with today's regeneration reversed out of it.
#
# Bash version matters here as it does for setup-env: Apple ships 3.2, Homebrew ships 5.x, and the
# script under test must work on both plus Debian on the appliance. Run it both ways:
#
#     /bin/bash tests/carry-enrolment.test.sh
#     tests/carry-enrolment.test.sh
set -uo pipefail

tests_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$tests_dir/.." && pwd)"
script="$repo_root/tools/carry-enrolment.sh"
filter="${1:-}"

# English and byte collation, for the same reasons setup-env.test.sh pins them: prose assertions
# below are written in English, and the script's own comparisons are byte-ordered.
export LC_ALL=C

passed=0
failed=0
current=""
scratch=""

if ! command -v sqlite3 >/dev/null 2>&1; then
    echo "sqlite3 is not installed; these tests cannot run." >&2
    exit 1
fi

# ------------------------------------------------------------------------------------------------
# Harness
# ------------------------------------------------------------------------------------------------

fail() {
    failed=$((failed + 1))
    echo "  FAIL  $current"
    echo "        $1"
    [ $# -gt 1 ] && printf '        expected: %s\n        actual:   %s\n' "$2" "$3"
    return 0
}

assert_eq() {
    if [ "$1" = "$2" ]; then
        passed=$((passed + 1))
    else
        fail "${3:-values differ}" "$1" "$2"
    fi
}

# For prose the script prints. Wrapped to fit 80 columns, so an assertion of more than a few words
# can span a line break; both sides are collapsed so these assert on what was said rather than on
# where it happened to wrap.
assert_says() {
    local haystack needle
    haystack="$(echo "$1" | tr '\n' ' ' | tr -s ' ' | sed 's/^ *//; s/ *$//')"
    needle="$(echo "$2" | tr '\n' ' ' | tr -s ' ' | sed 's/^ *//; s/ *$//')"
    case "$haystack" in
        *"$needle"*) passed=$((passed + 1)) ;;
        *) fail "${3:-not said}" "…${needle}…" "$1" ;;
    esac
}

refute_says() {
    local haystack needle
    haystack="$(echo "$1" | tr '\n' ' ' | tr -s ' ' | sed 's/^ *//; s/ *$//')"
    needle="$(echo "$2" | tr '\n' ' ' | tr -s ' ' | sed 's/^ *//; s/ *$//')"
    case "$haystack" in
        *"$needle"*) fail "${3:-said, and should not have}" "not …${needle}…" "$1" ;;
        *) passed=$((passed + 1)) ;;
    esac
}

test_case() {
    current="$1"
    case "$current" in
        *"$filter"*) ;;
        *) current=""; return 1 ;;
    esac
    echo "- $current"
    scratch="$(mktemp -d "${TMPDIR:-/tmp}/carry-enrolment.XXXXXX")"
    return 0
}

stamp_of() {
    sqlite3 "$1" "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;"
}

# ------------------------------------------------------------------------------------------------
# Fixtures
#
# The shape of an EF database rather than a plausible one: a history table, quoted identifiers, and
# ONE COLUMN PER LINE. The last of those is load-bearing - carry-enrolment reads a new column's
# definition verbatim out of CREATE TABLE rather than rebuilding it from PRAGMA table_info, so a
# fixture written on one line would exercise the refusal path instead of the apply path and quietly
# test nothing.
# ------------------------------------------------------------------------------------------------

# seed=1 puts rows in Printers, seed=0 leaves the database empty. The reference is the empty one,
# which is what --write-schema really produces - and it also keeps a NOT NULL column added on the
# reference side from failing an INSERT that has no value for it.
make_db() {
    local path=$1 stamp=$2 printers_extra=${3:-} extra_sql=${4:-} seed=${5:-1}

    sqlite3 "$path" <<SQL
CREATE TABLE "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);
INSERT INTO "__EFMigrationsHistory" VALUES ('$stamp', '10.0.0');

CREATE TABLE "Printers" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Printers" PRIMARY KEY AUTOINCREMENT,
    "Uuid" TEXT NOT NULL,
    "Name" TEXT NULL${printers_extra:+,}
$printers_extra
);

CREATE TABLE "PrusaConnectAuthentication" (
    "PrinterId" INTEGER NOT NULL CONSTRAINT "PK_PrusaConnectAuthentication" PRIMARY KEY,
    "Fingerprint" TEXT NOT NULL,
    "TokenHash" TEXT NOT NULL,
    CONSTRAINT "FK_PrusaConnectAuthentication_Printers_PrinterId" FOREIGN KEY ("PrinterId") REFERENCES "Printers" ("Id") ON DELETE CASCADE
);
CREATE INDEX "IX_Printers_Uuid" ON "Printers" ("Uuid");
$extra_sql
SQL

    [ "$seed" = "1" ] || return 0

    sqlite3 "$path" <<SQL
INSERT INTO "Printers" ("Uuid", "Name") VALUES ('uuid-one', 'MK3.5');
INSERT INTO "Printers" ("Uuid", "Name") VALUES ('uuid-two', 'Core One');
INSERT INTO "PrusaConnectAuthentication" VALUES (1, 'fingerprint-one', 'hash-one');
SQL
}

# The pair every case starts from: same schema, different stamp. A case then edits one side.
make_pair() {
    make_db "$scratch/old.sqlite" "20260820162112_InitialCreate" "${1:-}" "${2:-}" 1
    make_db "$scratch/new.sqlite" "20260821010838_InitialCreate" "${3:-}" "${4:-}" 0
}

echo
echo "carry-enrolment.sh — $BASH_VERSION"
echo

# ------------------------------------------------------------------------------------------------
# check reports, and changes nothing
# ------------------------------------------------------------------------------------------------

if test_case "check says nothing to do when schema and stamp already agree"; then
    make_db "$scratch/old.sqlite" "20260821010838_InitialCreate" "" "" 1
    make_db "$scratch/new.sqlite" "20260821010838_InitialCreate" "" "" 0

    out="$("$script" check "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"

    assert_says "$out" "Identical schema, identical stamp. Nothing to do."
fi

if test_case "check names a regeneration that changed nothing but the id"; then
    make_pair

    out="$("$script" check "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"

    assert_says "$out" "Identical schema, different stamp"
    assert_says "$out" "rewrites the history row and that is the whole fix"
fi

if test_case "check leaves the stamp alone"; then
    make_pair "" "" '    "Location" TEXT NULL'

    "$script" check "$scratch/old.sqlite" "$scratch/new.sqlite" >/dev/null 2>&1

    assert_eq "20260820162112_InitialCreate" "$(stamp_of "$scratch/old.sqlite")" "check is read-only"
fi

# ------------------------------------------------------------------------------------------------
# upgrade applies what is additive
# ------------------------------------------------------------------------------------------------

if test_case "upgrade adds a nullable column and stamps, keeping the rows"; then
    make_pair "" "" '    "Location" TEXT NULL'

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"

    assert_says "$out" "+ column Printers.Location TEXT NULL"
    assert_eq "20260821010838_InitialCreate" "$(stamp_of "$scratch/old.sqlite")" "stamped"
    assert_eq "2" "$(sqlite3 "$scratch/old.sqlite" 'SELECT COUNT(*) FROM "Printers";')" "rows kept"
    assert_eq "MK3.5" "$(sqlite3 "$scratch/old.sqlite" 'SELECT "Name" FROM "Printers" WHERE "Id" = 1;')" "values kept"
    assert_eq "" "$(sqlite3 "$scratch/old.sqlite" 'SELECT COALESCE("Location", "") FROM "Printers" WHERE "Id" = 1;')" "new column null"
fi

if test_case "upgrade adds a NOT NULL column when it has a default"; then
    make_pair "" "" '    "Ready" INTEGER NOT NULL DEFAULT 0'

    "$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" >/dev/null 2>&1

    assert_eq "20260821010838_InitialCreate" "$(stamp_of "$scratch/old.sqlite")" "stamped"
    assert_eq "0" "$(sqlite3 "$scratch/old.sqlite" 'SELECT "Ready" FROM "Printers" WHERE "Id" = 1;')" "default applied"
fi

if test_case "upgrade adds a whole table"; then
    make_pair "" "" "" '
CREATE TABLE "Cameras" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Cameras" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL
);'

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"

    assert_says "$out" "+ table Cameras"
    assert_eq "0" "$(sqlite3 "$scratch/old.sqlite" 'SELECT COUNT(*) FROM "Cameras";')" "table exists and is empty"
    assert_eq "20260821010838_InitialCreate" "$(stamp_of "$scratch/old.sqlite")" "stamped"
fi

if test_case "upgrade adds an index"; then
    make_pair "" "" "" 'CREATE INDEX "IX_Printers_Name" ON "Printers" ("Name");'

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"

    assert_says "$out" "+ index IX_Printers_Name"
    assert_eq "IX_Printers_Name" \
        "$(sqlite3 "$scratch/old.sqlite" "SELECT name FROM sqlite_master WHERE type='index' AND name='IX_Printers_Name';")" \
        "index created"
fi

if test_case "an index upgrade survives the next run - the trailing-newline trap"; then
    # EF writes its DDL with a trailing newline, and `upgrade` copies `sql` verbatim to create the
    # index. Rendering then turned that newline into a trailing space, so the repaired database
    # compared one byte longer than the reference it had just been made to match and the same index
    # came back as one addition AND one removal - blocking, on a database that was correct. Twice on
    # the appliance before it was found, both times advising `adopt` and the loss of everything a
    # stamp would have kept. This asserts the SECOND run, which is where it showed.
    make_pair "" "" "" 'CREATE INDEX "IX_Printers_Name" ON "Printers" ("Name");
'

    "$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" >/dev/null 2>&1
    out="$("$script" check "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"

    assert_says "$out" "Identical schema, identical stamp. Nothing to do."
    refute_says "$out" "the reference does not" "an index it just created is not reported as unexpected"
fi

if test_case "upgrade backs up first, and the backup still holds the old stamp"; then
    make_pair "" "" '    "Location" TEXT NULL'

    "$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" >/dev/null 2>&1

    assert_eq "20260820162112_InitialCreate" \
        "$(stamp_of "$scratch/old.sqlite.before-carry-enrolment")" "backup predates the stamp"
fi

if test_case "upgrade --dry-run changes nothing at all"; then
    make_pair "" "" '    "Location" TEXT NULL'
    before="$(sqlite3 "$scratch/old.sqlite" .dump)"

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" --dry-run 2>&1)"

    assert_says "$out" "--dry-run: nothing was changed."
    assert_eq "$before" "$(sqlite3 "$scratch/old.sqlite" .dump)" "byte-identical afterwards"
fi

# ------------------------------------------------------------------------------------------------
# upgrade refuses everything else — and, in every case, does not stamp
#
# The second assertion in each of these is the one that matters. A refusal that printed a complaint
# and stamped anyway would look right in the output and leave exactly the database this tool exists
# to prevent.
# ------------------------------------------------------------------------------------------------

if test_case "upgrade refuses a changed column type and points at adopt"; then
    make_pair "    \"Status\" INTEGER NOT NULL DEFAULT 0" "" "    \"Status\" TEXT NOT NULL DEFAULT 'Idle'"

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"
    status=$?

    assert_eq "1" "$status" "fails"
    assert_says "$out" "Printers.Status is declared INTEGER NOT NULL"
    assert_says "$out" "TEXT NOT NULL"
    assert_says "$out" "A type change in particular can never be stamped across"
    assert_says "$out" "carry-enrolment.sh adopt"
    assert_eq "20260820162112_InitialCreate" "$(stamp_of "$scratch/old.sqlite")" "NOT stamped"
fi

if test_case "upgrade refuses a NOT NULL column with no default"; then
    make_pair "" "" '    "Location" TEXT NOT NULL'

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"
    status=$?

    assert_eq "1" "$status" "fails"
    assert_says "$out" "Printers.Location is new, NOT NULL and has no default"
    assert_eq "20260820162112_InitialCreate" "$(stamp_of "$scratch/old.sqlite")" "NOT stamped"
fi

if test_case "upgrade refuses a column the old database has and the reference does not"; then
    make_pair '    "Retired" INTEGER NULL' "" ""

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"
    status=$?

    assert_eq "1" "$status" "fails"
    assert_says "$out" "the old database has a column the reference does not: Printers.Retired"
    assert_eq "20260820162112_InitialCreate" "$(stamp_of "$scratch/old.sqlite")" "NOT stamped"
fi

if test_case "upgrade refuses a table the old database has and the reference does not"; then
    make_pair "" 'CREATE TABLE "Legacy" ("Id" INTEGER NOT NULL PRIMARY KEY);' "" ""

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"
    status=$?

    assert_eq "1" "$status" "fails"
    assert_says "$out" "the old database has a table the reference does not: Legacy"
    assert_eq "20260820162112_InitialCreate" "$(stamp_of "$scratch/old.sqlite")" "NOT stamped"
fi

if test_case "upgrade refuses an index the old database has and the reference does not"; then
    make_pair "" 'CREATE INDEX "IX_Printers_Name" ON "Printers" ("Name");' "" ""

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"
    status=$?

    assert_eq "1" "$status" "fails"
    assert_says "$out" "the old database has index IX_Printers_Name and the reference does not"
    assert_eq "20260820162112_InitialCreate" "$(stamp_of "$scratch/old.sqlite")" "NOT stamped"
fi

if test_case "upgrade refuses a redefined index rather than silently keeping either"; then
    make_pair "" 'CREATE INDEX "IX_Printers_Both" ON "Printers" ("Uuid");' \
              "" 'CREATE INDEX "IX_Printers_Both" ON "Printers" ("Name");'

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"
    status=$?

    assert_eq "1" "$status" "fails"
    assert_says "$out" "the old database has index IX_Printers_Both and the reference does not"
    assert_eq "20260820162112_InitialCreate" "$(stamp_of "$scratch/old.sqlite")" "NOT stamped"
fi

if test_case "upgrade refuses a new table-level constraint, which ALTER TABLE cannot add"; then
    make_pair "" "" "" ""
    # A UNIQUE the reference's Printers has and the old one does not. Its columns are identical, so
    # only the constraint comparison can see this - which is the point of having one.
    sqlite3 "$scratch/new.sqlite" <<'SQL'
ALTER TABLE "Printers" RENAME TO "Printers_old";
CREATE TABLE "Printers" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Printers" PRIMARY KEY AUTOINCREMENT,
    "Uuid" TEXT NOT NULL,
    "Name" TEXT NULL,
    CONSTRAINT "AK_Printers_Uuid" UNIQUE ("Uuid")
);
DROP TABLE "Printers_old";
SQL

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"
    status=$?

    assert_eq "1" "$status" "fails"
    assert_says "$out" "Printers's table-level constraints differ from the reference's"
    assert_eq "20260820162112_InitialCreate" "$(stamp_of "$scratch/old.sqlite")" "NOT stamped"
fi

if test_case "upgrade refuses a new primary-key column"; then
    make_pair "" "" "" '
CREATE TABLE "Slots" (
    "PrinterId" INTEGER NOT NULL,
    "Slot" INTEGER NOT NULL,
    CONSTRAINT "PK_Slots" PRIMARY KEY ("PrinterId", "Slot")
);'
    sqlite3 "$scratch/old.sqlite" 'CREATE TABLE "Slots" ("PrinterId" INTEGER NOT NULL CONSTRAINT "PK_Slots" PRIMARY KEY);'

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"
    status=$?

    assert_eq "1" "$status" "fails"
    assert_says "$out" "Slots.Slot is new and part of the primary key"
    assert_eq "20260820162112_InitialCreate" "$(stamp_of "$scratch/old.sqlite")" "NOT stamped"
fi

if test_case "a refusal reports every difference, not only the first"; then
    make_pair '    "Retired" INTEGER NULL' "" '    "Location" TEXT NOT NULL'

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"

    assert_says "$out" "Printers.Location is new, NOT NULL and has no default"
    assert_says "$out" "the old database has a column the reference does not: Printers.Retired"
fi

# ------------------------------------------------------------------------------------------------
# The transaction
# ------------------------------------------------------------------------------------------------

if test_case "a statement that fails rolls the whole upgrade back, stamp included"; then
    # A unique index the existing rows violate. The comparison cannot know that - uniqueness is a
    # fact about the data, not the schema - so this is the case that proves the apply is one
    # transaction rather than a sequence of statements that happens to usually work.
    make_pair "" "" "" 'CREATE UNIQUE INDEX "IX_Printers_Name_Unique" ON "Printers" ("Name");'
    sqlite3 "$scratch/old.sqlite" <<'SQL'
UPDATE "Printers" SET "Name" = 'the same name twice';
SQL
    before="$(sqlite3 "$scratch/old.sqlite" .dump)"

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"
    status=$?

    assert_eq "1" "$status" "fails"
    assert_says "$out" "the upgrade failed and was rolled back"
    assert_eq "$before" "$(sqlite3 "$scratch/old.sqlite" .dump)" "database byte-identical"
    assert_eq "20260820162112_InitialCreate" "$(stamp_of "$scratch/old.sqlite")" "NOT stamped"
fi

if test_case "upgrade refuses a database another process holds"; then
    make_pair "" "" '    "Location" TEXT NULL'

    # A writer holding the database, the way a running Homespool would. Held open through a fifo
    # rather than by passing SQL on the command line: sqlite3 commits and exits at the end of its
    # argument, so a one-shot invocation holds the lock for no measurable time.
    mkfifo "$scratch/hold"
    sqlite3 "$scratch/old.sqlite" < "$scratch/hold" >/dev/null 2>&1 &
    holder=$!
    exec 9>"$scratch/hold"
    echo "BEGIN IMMEDIATE; INSERT INTO \"Printers\" (\"Uuid\") VALUES ('held');" >&9
    sleep 1

    out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/new.sqlite" 2>&1)"
    status=$?

    exec 9>&-
    wait "$holder" 2>/dev/null

    assert_eq "1" "$status" "fails"
    assert_says "$out" "is locked. Stop Homespool first"
fi

# ------------------------------------------------------------------------------------------------
# The real schema
#
# Everything above uses a four-table fixture. This one uses the thirty-odd tables the application
# actually has, produced by the build itself through --write-schema, with the 2026-08-21 regeneration
# reversed out of a copy: the two Cameras credential columns removed and the id put back. That is the
# incident this subcommand was written for, reproduced mechanically from the schema rather than from
# a diff of two migration files in git.
#
# Skipped, not failed, when dotnet is absent - the appliance running this has sqlite3 and no SDK, and
# a suite that could not run there would be the wrong way round.
# ------------------------------------------------------------------------------------------------

if test_case "the 2026-08-21 regeneration, against the schema this build really produces"; then
    if ! command -v dotnet >/dev/null 2>&1; then
        echo "        SKIP  dotnet is not on PATH"
    else
        if ! dotnet run --project "$repo_root/Homespool.Host" -- \
                --write-schema "$scratch/reference.sqlite" >/dev/null 2>&1; then
            fail "--write-schema did not produce a database"
        else
            expected="$(stamp_of "$scratch/reference.sqlite")"

            # The appliance's database as it was before the image moved: same schema minus the two
            # columns commit 8f60c24 added, and stamped with the id it carried.
            cp "$scratch/reference.sqlite" "$scratch/old.sqlite"
            sqlite3 "$scratch/old.sqlite" <<'SQL'
ALTER TABLE "Cameras" DROP COLUMN "CredentialUser";
ALTER TABLE "Cameras" DROP COLUMN "CredentialSecret";
UPDATE "__EFMigrationsHistory" SET "MigrationId" = '20260820162112_InitialCreate';
SQL

            out="$("$script" upgrade "$scratch/old.sqlite" "$scratch/reference.sqlite" 2>&1)"
            status=$?

            assert_eq "0" "$status" "succeeds"
            assert_says "$out" "+ column Cameras.CredentialUser TEXT NULL"
            assert_says "$out" "+ column Cameras.CredentialSecret TEXT NULL"
            refute_says "$out" "NOT additive" "nothing blocks"
            assert_eq "$expected" "$(stamp_of "$scratch/old.sqlite")" "stamped to this build's id"
            assert_eq "ok" "$(sqlite3 "$scratch/old.sqlite" 'PRAGMA integrity_check;')" "intact"

            # And the comparison agrees with itself afterwards, which is the check upgrade runs
            # internally - asserted here too so a change that broke it could not pass quietly.
            assert_says "$("$script" check "$scratch/old.sqlite" "$scratch/reference.sqlite" 2>&1)" \
                "Identical schema, identical stamp"
        fi
    fi
fi

# ------------------------------------------------------------------------------------------------

echo
if [ "$failed" -eq 0 ]; then
    echo "$passed assertions passed."
else
    echo "$failed failed, $passed passed."
    exit 1
fi
