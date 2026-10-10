#!/bin/sh
# Report whether newer Homespool images are published than the ones running here, and what pulling
# them would bring. Reports only: it never pulls, never restarts, never changes the deployment.
#
#   homespool-update-check collect    # root: what is running, from the Docker socket
#   homespool-update-check compare    # unprivileged: what is published, and the report
#   homespool-update-check publish    # root: the report into the application's volume
#   homespool-update-check watch      # root: start the three again when the running images changed
#
# Run daily from homespool-update-check.timer, which runs the three in that order as one service. To
# run it by hand, start the service; the steps are not meant to be run on their own.
#
# AND AGAIN WHENEVER THE RUNNING IMAGES CHANGE. The report describes the containers that were running
# when it was written, so a pull leaves it describing the ones just replaced - telling whoever took the
# update to take it. homespool-update-check-watch.timer runs watch every few minutes: it asks Docker
# which image each container runs, by id, and starts the service when that is not the list collect
# last wrote. Nothing leaves the machine, and no JSON is read. A run that fails after collect is not
# retried by watch - the list is already this one - but by the next daily run, so a registry that is
# down is not asked every few minutes.
#
# THREE STEPS, BECAUSE ONLY TWO OF THEM NEED ROOT, AND THOSE TWO TOUCH NOTHING FROM OUTSIDE.
#
#   collect - root, for the Docker socket. Which containers run which images, and each image's own
#     configuration, written as Docker prints it into $RUNTIME_DIRECTORY. No network, and no jq.
#   compare - the unit's unprivileged user, sandboxed, with no Docker socket. Every request that leaves
#     the machine and every byte of JSON that comes back is handled here: the registry, read through
#     `docker buildx imagetools`, which needs no daemon; GitHub; Microsoft's release metadata. It
#     writes the report into $STATE_DIRECTORY/update-check.json, replaced whole.
#   publish - root, for the application's volume, which only root can reach. It copies the report in,
#     and trusts nothing the unprivileged step could have written except the report itself, which it
#     checks: see publish below.
#
# WHAT IT COMPARES. The running app, proxy and camera sidecar containers are found by their compose
# labels - no compose file is read, so nothing an operator can edit decides what the root steps run.
# Each one names the image it was started from, say registry.example.net/homespool, which is the tag
# the deployment follows (latest when none is written). The registry's current digest for that tag
# against the running image's is the whole of "is there something newer". When there is:
#
#   - Homespool's commits between the running revision and the published one that touch what the
#     image is built from, counted by type, merges left out. The image's context.path label names
#     that - the camera sidecar is built from go2rtc/, so a fix to the application is not one of its
#     fixes - and an image without it is counted against the whole repository. It asks GitHub for the
#     history of the PUBLISHED revision - which every deployment following that tag asks for - and
#     finds its own revision in the answer, so the one thing that would identify this deployment's
#     build, and with it its known defects, never leaves the machine. A page at a time, up to
#     history_pages of them; a running revision further back than that is said to be, and the count
#     becomes a floor.
#   - The .NET runtime, when the published image carries a newer one: every release in between, from
#     Microsoft's release metadata, and whether it was a security release.
#   - The Go modules compiled into the camera sidecar, when the published image took newer ones.
#   - Whether the published image is built on a different base. A newer image on the same revision
#     is a rebuild, and a rebuild is published for what it fixes underneath.
#   - For the application, the commits that changed compose.yaml, which no pull brings.
#
# A NEWER IMAGE OF THE SAME SOURCE IS NOT NECESSARILY NEWS. Every publish builds all three images and
# stamps each with the commit, so an image whose own source did not change still gets a new digest.
# Each image labels the git tree of its build context and every other input it was built from, so:
# all of them equal, and the published image is this one stamped with another commit - restamped,
# not newer. The same tree with another input - packages refreshed on another day, another
# toolchain - is newer, and says so only for what the list above counts.
#
# A running revision only counts when the running image's source label names the same repository as
# the published one. An image built before Homespool labelled itself inherits its base's labels, and
# nginx's commit is not a place in Homespool's history.
#
# NEEDS NO CREDENTIALS, and must not. The registry has to allow anonymous reads of these images -
# GHCR's public packages do. An image with no registry in its name was built from source rather than
# pulled - where the stack runs, or on the machine that made a card - so it has nothing published to
# compare with, and is reported as such, with the date it was built, without any request leaving. So
# is one named for a registry that never served it: no digest recorded for its repository, because
# it was built or loaded here.
#
# A SOURCE THAT CANNOT BE REACHED FAILS THE RUN, and the previous report stays: a report written from
# part of the evidence would say "nothing to take" for the part it could not see.
#
# HOMESPOOL_PROJECT is the compose project, set by the unit. Needs docker with buildx, curl, jq, and
# for publish setpriv, dd, stat and timeout.
set -eu

project="${HOMESPOOL_PROJECT:-homespool}"
# The compose services whose images are Homespool's, in the order the report lists them.
services="homespool proxy go2rtc"
state_dir="${STATE_DIRECTORY:-/var/lib/homespool}"
run_dir="${RUNTIME_DIRECTORY:-/run/homespool-update-check}"
# Root's, and nobody else's: collect writes the running image ids here for watch to compare with. Not
# the state directory, which the unprivileged step owns and could rewrite to start runs or stop them.
watch_dir="${HOMESPOOL_WATCH_DIRECTORY:-/var/lib/homespool-update-check}"
dotnet_index="https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json"
# The prefix of the labels Homespool's builds set where OCI has no key.
ns="io.github.henrikottesorensen.homespool"
# How much of the published revision's history it reads, in GitHub's pages of 100, looking for the
# running one. Each page is a request, and GitHub allows 60 an hour without a login.
history_pages=5

# The largest report publish will copy. A real one is a few kilobytes.
report_limit=1048576

die() {
    echo "homespool-update-check: $*" >&2
    exit 1
}

require() {
    for tool in "$@"; do
        command -v "$tool" >/dev/null 2>&1 || die "$tool is needed and is not on PATH"
    done
}

# Never piped: dash has no pipefail, so `fetch | jq` would report jq's success after curl had failed,
# and the report would be written without the part it could not fetch. Into a file, then jq.
fetch() {
    curl -fsSL --max-time 60 "$1" || die "could not fetch $1"
}

# Empty when the service has no container; a failed docker ps is not that, and dies. Not piped into head
# for the same reason as fetch: the pipeline would report head's success. Always called as the whole of an
# assignment, so the failure reaches set -e.
running_container() {
    ids="$(docker ps --filter "label=com.docker.compose.project=$project" \
        --filter "label=com.docker.compose.service=$1" --format '{{.ID}}')" ||
        die "docker ps failed, so whether $1 is running is not known"
    printf '%s\n' "$ids" | head -n 1
}

# What watch compares: a line per service, its name and its container's image id, or - when none runs.
# collect writes its list with this too, and inspects the image the line names, so the list is exactly
# what it collected.
image_line() {
    if [ -n "$2" ]; then
        printf '%s %s\n' "$1" "$(docker inspect --format '{{.Image}}' "$2")"
    else
        printf '%s -\n' "$1"
    fi
}

# --- collect: root, the Docker socket, nothing from outside ------------------------------------------

collect() {
    require docker
    mkdir -p "$run_dir"
    docker version --format '{{.Server.Os}}/{{.Server.Arch}}' > "$run_dir/platform"

    # For watch, kept past the run where the runtime directory is not, and never in it: the
    # unprivileged step can write there. Replaced whole, so watch never compares with half a list.
    mkdir -p "$watch_dir"
    chmod 0700 "$watch_dir"
    images="$watch_dir/.images.new"
    : > "$images"

    for service in $services; do
        rm -f "$run_dir/$service.reference" "$run_dir/$service.image.json"
        container="$(running_container "$service")"
        line="$(image_line "$service" "$container")"
        printf '%s\n' "$line" >> "$images"
        [ -n "$container" ] || continue

        docker inspect --format '{{.Config.Image}}' "$container" > "$run_dir/$service.reference"
        docker image inspect --format '{{json .}}' "${line#* }" > "$run_dir/$service.image.json"
    done

    mv -f "$images" "$watch_dir/images"
}

# --- compare: unprivileged, everything from outside ----------------------------------------------------

# An image's label or environment variable, from either spelling of its configuration: the registry's
# answer says config, Docker's own Config.
label() { jq -r --arg k "$1" '(.config.Labels // .Config.Labels // {})[$k] // ""' "$2"; }
envvar() { jq -r --arg k "$1=" '((.config.Env // .Config.Env // [])[] | select(startswith($k)) | ltrimstr($k)) // ""' "$2"; }

# Everything an image was built from besides the commit it is stamped with, as one line: two images
# with the same line differ in that stamp alone. A key an image does not have reads as empty, so the
# proxy, which has no toolchain, compares its tree and base.
inputs() {
    jq -r --arg ns "$ns" '(.config.Labels // .Config.Labels // {}) as $l
        | ["\($ns).context.tree", "org.opencontainers.image.base.digest", "\($ns).builder.digest",
           "\($ns).go.modules", "\($ns).packages.refreshed"]
        | map($l[.] // "") | join(" ")' "$1"
}

# The next page of a history, added to the file of pages so far. The page count and the last page's
# length sit beside it, for more.
page() {
    pages="$(cat "$1.pages" 2>/dev/null || echo 0)"
    pages=$((pages + 1))
    fetch "$2&per_page=100&page=$pages" > "$1.page"
    jq -s '.[0] + .[1]' "$1" "$1.page" > "$1.new"
    mv -f "$1.new" "$1"
    jq length "$1.page" > "$1.last"
    echo "$pages" > "$1.pages"
}

# Whether a history has more to read: nothing read yet, or a full last page and pages left.
more() {
    [ -f "$1.pages" ] || return 0
    [ "$(cat "$1.last")" -eq 100 ] && [ "$(cat "$1.pages")" -lt "$history_pages" ]
}

# What the published revision ($2) has and the running one ($3) does not, in the repository $1:
# everything the published revision's history reaches, less everything it reaches from the running
# one, walked along the parent links - not the list's order, which is by date and puts a branch merged
# late among commits long older than it. With a path ($4), only the commits that touch it, from
# GitHub's history of the published revision for that path. Counted by type, merges left out.
#
# The whole history is read once per published revision and shared by every image published from it,
# further only as far as the next running revision needs. A path's history is read until a page
# reaches back past what the first walk found missing.
changes() {
    all="$work/history.$2.json"
    [ -f "$all" ] || printf '[]\n' > "$all"
    while ! jq -e --arg r "$3" 'any(.[]; .sha == $r)' "$all" >/dev/null && more "$all"; do
        page "$all" "https://api.github.com/repos/$1/commits?sha=$2"
    done

    ahead="$work/ahead.$2.$3.json"
    jq --arg running "$3" '
            (map({key: .sha, value: [.parents[].sha]}) | from_entries) as $parents
            | ({seen: {}, queue: [$running]}
               | until(.queue | length == 0;
                     .queue[0] as $c
                     | .queue |= .[1:]
                     | if .seen[$c] or ($parents[$c] == null) then .
                       else .seen[$c] = true | .queue += $parents[$c] end)
               | .seen) as $behind
            | {
                found: ($parents[$running] != null),
                read: length,
                missing: (map(select(($behind[.sha] // false) | not) | {key: .sha, value: true}) | from_entries)
            }' "$all" > "$ahead"

    touched="$all"
    if [ -n "$4" ] && [ "$4" != "." ]; then
        touched="$work/history.$2.$(printf '%s' "$4" | tr -c 'A-Za-z0-9._-' '_').json"
        [ -f "$touched" ] || printf '[]\n' > "$touched"
        while more "$touched" &&
            { [ ! -f "$touched.page" ] ||
              jq -e --slurpfile a "$ahead" 'all(.[]; $a[0].missing[.sha] // false)' "$touched.page" >/dev/null; }; do
            page "$touched" "https://api.github.com/repos/$1/commits?sha=$2&path=$4"
        done
    fi

    jq --slurpfile a "$ahead" --arg path "${4:-.}" '
            $a[0] as $a
            | map(select(($a.missing[.sha] // false) and (.parents | length) == 1) | .commit.message | split("\n")[0])
            | {
                path: $path,
                commits: length,
                by_type: (map(capture("^(?<type>[a-z]+)(\\([^)]*\\))?!?:").type // "other")
                          | group_by(.) | map({key: .[0], value: length}) | from_entries),
                found: $a.found,
                read: $a.read
            }' "$touched"
}

compare() {
    require docker curl jq
    [ -s "$run_dir/platform" ] || die "nothing collected in $run_dir: collect runs first, as root"

    # imagetools keeps a little state in its configuration directory, and this user has no home. An
    # empty configuration is also what says no login is used.
    DOCKER_CONFIG="$run_dir/docker"
    export DOCKER_CONFIG
    mkdir -p "$DOCKER_CONFIG"
    [ -f "$DOCKER_CONFIG/config.json" ] || printf '{}\n' > "$DOCKER_CONFIG/config.json"

    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT INT TERM

    platform="$(cat "$run_dir/platform")"
    checked="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

    for service in $services; do
        entry="$work/$service.json"

        if [ ! -s "$run_dir/$service.reference" ]; then
            jq -n --arg s "$service" '{service: $s, status: "not-running"}' > "$entry"
            echo "$service: not running"
            continue
        fi

        reference="$(cat "$run_dir/$service.reference")"
        run="$run_dir/$service.image.json"
        built="$(jq -r '.Created // ""' "$run")"

        case "$reference" in
            *@*)
                jq -n --arg s "$service" --arg r "$reference" --arg b "$built" \
                    '{service: $s, reference: $r, status: "pinned", built: $b}' > "$entry"
                echo "$service: pinned to a digest ($reference), so there is no tag to follow"
                continue
                ;;
        esac

        # Docker's own rule: the first component names a registry only if it has a dot or a port, or
        # is localhost. Anything else was built from source, or would be Docker Hub's library, not us.
        case "$reference" in
            */*) first="${reference%%/*}" ;;
            *) first="" ;;
        esac
        case "$first" in
            *.* | *:* | localhost) ;;
            *)
                jq -n --arg s "$service" --arg r "$reference" --arg b "$built" \
                    '{service: $s, reference: $r, status: "local", built: $b}' > "$entry"
                echo "$service: built from source rather than pulled ($reference, $built), so nothing is published to compare with"
                continue
                ;;
        esac

        case "${reference##*/}" in
            *:*) ;;
            *) reference="$reference:latest" ;;
        esac
        repository="${reference%:*}"

        # A registry's name is not a registry's image. Docker records a digest under the repository
        # only when it pulls or pushes the image, so one without was built or loaded here - a card
        # built from a commit that is not a release carries GHCR's names on images built where the
        # card was made. Compared with what the registry serves it would always be "newer", and the
        # pull that asks for could be an older build.
        if ! jq -e --arg r "$repository@" '(.RepoDigests // []) | any(startswith($r))' "$run" >/dev/null; then
            jq -n --arg s "$service" --arg r "$reference" --arg b "$built" \
                '{service: $s, reference: $r, status: "local", built: $b}' > "$entry"
            echo "$service: built or loaded here rather than pulled ($reference, $built), so it is no image the registry served"
            continue
        fi

        # `print`, not a bare {{.Manifest.Digest}}: buildx before 0.33.0 answers any format starting
        # {{.Manifest with its human-readable summary instead, and Debian and Ubuntu still ship one.
        published_digest="$(docker buildx imagetools inspect --format '{{print .Manifest.Digest}}' "$reference")" ||
            die "could not read $reference from its registry"
        docker buildx imagetools inspect --format '{{json .Image}}' "$reference" > "$work/$service.published.json" ||
            die "could not read $reference's configuration from its registry"

        # One platform's configuration: a single manifest answers with it directly, an index with one
        # per platform, and this machine runs its own.
        jq --arg p "$platform" 'if has("config") then . else (.[$p] // (to_entries[0].value)) end' \
            "$work/$service.published.json" > "$work/$service.published.config.json"

        pub="$work/$service.published.config.json"
        published_revision="$(label org.opencontainers.image.revision "$pub")"
        published_source="$(label org.opencontainers.image.source "$pub")"
        running_revision="$(label org.opencontainers.image.revision "$run")"
        running_source="$(label org.opencontainers.image.source "$run")"
        [ "$running_source" = "$published_source" ] || running_revision=""
        published_base="$(label org.opencontainers.image.base.digest "$pub")"
        running_base="$(label org.opencontainers.image.base.digest "$run")"

        # The running revision even here, so a reader can see when the three containers run different
        # builds - one recreated without the others.
        if jq -e --arg d "$repository@$published_digest" '(.RepoDigests // []) | any(. == $d)' "$run" >/dev/null; then
            jq -n --arg s "$service" --arg r "$reference" --arg d "$published_digest" \
                --arg rr "$running_revision" --arg rb "$running_base" \
                '{service: $s, reference: $r, status: "current", digest: $d, running: {revision: $rr, base: $rb}}' > "$entry"
            echo "$service: current ($reference is $published_digest)"
            continue
        fi

        # --- the same source, stamped with another commit ---
        published_tree="$(label "$ns.context.tree" "$pub")"
        running_tree="$(label "$ns.context.tree" "$run")"
        [ "$running_source" = "$published_source" ] || running_tree=""
        same_source=""
        if [ -n "$published_tree" ] && [ "$published_tree" = "$running_tree" ]; then
            same_source=1
        fi
        if [ -n "$same_source" ] && [ "$(inputs "$pub")" = "$(inputs "$run")" ]; then
            jq -n --arg s "$service" --arg r "$reference" --arg d "$published_digest" \
                --arg pr "$published_revision" --arg rr "$running_revision" \
                --arg pb "$published_base" --arg rb "$running_base" '{
                    service: $s, reference: $r, status: "restamped", digest: $d,
                    running: {revision: $rr, base: $rb}, published: {revision: $pr, base: $pb}
                }' > "$entry"
            echo "$service: the published image is this one stamped with another commit ($reference is $published_digest)"
            continue
        fi

        # --- Homespool, from the published revision's history ---
        printf 'null\n' > "$work/$service.homespool.json"
        printf 'null\n' > "$work/$service.compose.json"
        slug="${published_source#https://github.com/}"
        # What the image is built from. A label is the publisher's word, and this one goes into a URL,
        # so anything but a plain path counts the whole repository instead.
        context_path="$(label "$ns.context.path" "$pub")"
        case "$context_path" in
            *[!A-Za-z0-9._/-]* | /* | *..*) context_path="" ;;
        esac
        if [ -n "$same_source" ] || { [ -n "$published_revision" ] && [ "$published_revision" = "$running_revision" ]; }; then
            printf '{"commits":0,"by_type":{},"found":true}\n' > "$work/$service.homespool.json"
        elif [ -n "$published_revision" ] && [ -n "$running_revision" ] && [ "$slug" != "$published_source" ]; then
            changes "$slug" "$published_revision" "$running_revision" "$context_path" > "$work/$service.homespool.json"
            if [ "$service" = homespool ]; then
                changes "$slug" "$published_revision" "$running_revision" compose.yaml > "$work/$service.compose.json"
            fi
        fi

        # --- the .NET runtime, when the published image carries a newer one ---
        printf 'null\n' > "$work/$service.runtime.json"
        running_aspnet="$(envvar ASPNET_VERSION "$run")"
        published_aspnet="$(envvar ASPNET_VERSION "$pub")"
        if [ -n "$running_aspnet" ] && [ -n "$published_aspnet" ] && [ "$running_aspnet" != "$published_aspnet" ]; then
            channel="${running_aspnet%.*}"
            fetch "$dotnet_index" > "$work/dotnet-index.json"
            releases_url="$(jq -r --arg c "$channel" \
                '.["releases-index"][] | select(.["channel-version"] == $c) | .["releases.json"]' "$work/dotnet-index.json")"
            [ -n "$releases_url" ] || die "the .NET release metadata has no channel $channel"
            fetch "$releases_url" > "$work/dotnet-$channel.json"
            jq --arg from "$running_aspnet" --arg to "$published_aspnet" '
                    def version: split(".") | map(tonumber);
                    {
                        from: $from,
                        to: $to,
                        releases: [
                            .releases[]
                            | select(.["release-version"] | test("^[0-9]+\\.[0-9]+\\.[0-9]+$"))
                            | select((.["release-version"] | version) > ($from | version)
                                     and (.["release-version"] | version) <= ($to | version))
                            | {version: .["release-version"], security: (.security == true),
                               cves: [.["cve-list"][]?["cve-id"]]}
                        ]
                    }' "$work/dotnet-$channel.json" > "$work/$service.runtime.json"
        fi

        # The Go modules are a reason and the toolchain is not: the sidecar takes each module's latest
        # release, which comes out about monthly and changes what serves its published ports, where the
        # toolchain image is republished for much that never reaches the binary.
        jq -n --arg s "$service" --arg r "$reference" --arg d "$published_digest" \
            --arg pr "$published_revision" --arg rr "$running_revision" \
            --arg pb "$published_base" --arg rb "$running_base" \
            --arg pm "$(label "$ns.go.modules" "$pub")" --arg rm "$(label "$ns.go.modules" "$run")" \
            --slurpfile homespool "$work/$service.homespool.json" \
            --slurpfile compose "$work/$service.compose.json" \
            --slurpfile runtime "$work/$service.runtime.json" '
            ($homespool[0]) as $h | ($runtime[0]) as $rt | ($compose[0]) as $c
            | def fixes($n): "\($n) Homespool fix\(if $n == 1 then "" else "es" end)";
            {
                service: $s,
                reference: $r,
                status: "newer",
                digest: $d,
                running: {revision: $rr, base: $rb},
                published: {revision: $pr, base: $pb},
                homespool: $h,
                runtime: $rt,
                base_changed: ($pb != "" and $rb != "" and $rb != $pb),
                reasons: [
                    (if $rr == "" then "the running image does not say which Homespool revision it is" else empty end),
                    (if $h == null then empty
                     else ($h.by_type.fix // 0) as $f
                     | if $h.found then (if $f > 0 then fixes($f) else empty end)
                       elif $f > 0 then "at least \(fixes($f)) (the running revision is older than the \($h.read) commits read)"
                       else "a running revision older than the \($h.read) commits read, so what changed since is not known" end
                     end),
                    (if $rt != null then ($rt.releases[] | select(.security) | ".NET \(.version), a security release") else empty end),
                    (if $pm != "" and $rm != "" and $pm != $rm
                     then "compiled with newer Go modules: \(($pm | split(" ")) - ($rm | split(" ")) | join(", "))"
                     else empty end),
                    (if $pr != "" and $pr == $rr then "rebuilt from the same revision on newer packages" else empty end),
                    (if $pb != "" and $rb != "" and $rb != $pb and $pr != $rr then "built on a newer base" else empty end)
                ]
            }
            + (if $c != null then {compose: $c} else {} end)' > "$entry"

        echo "$service: newer image published ($reference is $published_digest): $(jq -r '.reasons | if length == 0 then "nothing it names" else join("; ") end' "$entry")"
    done

    jq -s --arg checked "$checked" \
        '{schema: 1, checked: $checked, update_available: any(.[]; .status == "newer"), services: .}' \
        $(for service in $services; do printf '%s ' "$work/$service.json"; done) > "$work/update-check.json"

    # Replaced whole, never rewritten in place, so a reader never sees half of one.
    mkdir -p "$state_dir"
    cp "$work/update-check.json" "$state_dir/.update-check.json.new"
    chmod 0644 "$state_dir/.update-check.json.new"
    mv -f "$state_dir/.update-check.json.new" "$state_dir/update-check.json"
}

# --- publish: root, the application's volume -----------------------------------------------------------

# The report was written by the unprivileged step, into a directory it owns, so root treats it as that
# step's word and nothing more. A snapshot first, so what is checked is what is copied: dd refuses a
# symlink in the report's place (iflag=nofollow), reads no more than the limit, and cannot be held on
# a FIFO for longer than the timeout. Then the snapshot's size, and its shape - parsed by jq as the
# directory's owner rather than as root, because a parser fed hostile bytes is exactly what the split
# keeps away from root. The volume is asked of Docker again rather than read from anything the
# unprivileged step could write, or it could name where root writes.
publish() {
    require docker jq setpriv dd stat timeout

    source_file="$state_dir/update-check.json"
    owner="$(stat -c '%u:%g' "$state_dir")"
    case "$owner" in
        0:*) die "$state_dir is root's, so the report cannot be checked as anybody else: refusing to publish it" ;;
    esac

    snapshot_dir="$(mktemp -d)"
    trap 'rm -rf "$snapshot_dir"' EXIT INT TERM
    snapshot="$snapshot_dir/update-check.json"

    timeout 30 dd if="$source_file" of="$snapshot" iflag=nofollow bs="$report_limit" count=1 status=none 2>/dev/null ||
        die "could not take the report from $source_file: not a plain file, or not there"

    size="$(stat -c '%s' "$snapshot")"
    [ "$size" -gt 0 ] || die "the report in $source_file is empty"
    [ "$size" -lt "$report_limit" ] || die "the report in $source_file is $report_limit bytes or more; a real one is a few kilobytes"

    setpriv --reuid="${owner%:*}" --regid="${owner#*:}" --clear-groups --no-new-privs \
        jq -e '.schema == 1 and (.services | type == "array")' < "$snapshot" >/dev/null ||
        die "the report in $source_file is not a report"

    # The copy the application shows: the volume compose.yaml mounts read-only into its container at
    # /var/lib/homespool, found through that container's own mounts rather than by a volume name rebuilt
    # here from the project. None - a compose.yaml from before the volume, or no application running -
    # and there is nobody to hand it to. World-readable, because the application does not run as root.
    container="$(running_container homespool)"
    [ -n "$container" ] || { echo "the application is not running, so it is not handed the report"; return 0; }
    volume="$(docker inspect \
        --format '{{range .Mounts}}{{if eq .Destination "/var/lib/homespool"}}{{.Source}}{{end}}{{end}}' \
        "$container")"
    [ -n "$volume" ] && [ -d "$volume" ] ||
        { echo "the application has no report volume - a compose.yaml from before it - so it is not handed the report"; return 0; }

    cp "$snapshot" "$volume/.update-check.json.new"
    chmod 0644 "$volume/.update-check.json.new"
    mv -f "$volume/.update-check.json.new" "$volume/update-check.json"
}

# --- watch: root, the Docker socket, nothing from outside ---------------------------------------------

watch() {
    require docker systemctl
    current=""
    for service in $services; do
        container="$(running_container "$service")"
        current="$current$(image_line "$service" "$container")
"
    done
    current="${current%?}"

    if [ -f "$watch_dir/images" ] && [ "$current" = "$(cat "$watch_dir/images")" ]; then
        return 0
    fi

    echo "the running images are not the ones the last check looked at, so it runs again"
    systemctl start --no-block homespool-update-check.service
}

case "${1:-}" in
    collect) collect ;;
    compare) compare ;;
    publish) publish ;;
    watch) watch ;;
    *)
        echo "usage: $0 collect|compare|publish|watch - start homespool-update-check.service to run the first three" >&2
        exit 2
        ;;
esac
