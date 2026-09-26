#!/bin/sh
# Report whether newer Homespool images are published than the ones running here, and what pulling
# them would bring. Reports only: it never pulls, never restarts, never changes the deployment.
#
# Run daily from homespool-update-check.timer. The answer goes to the journal, a line per container,
# and as JSON to $STATE_DIRECTORY/update-check.json (/var/lib/homespool when run by hand), replaced
# atomically, so a reader never sees half of one.
#
# WHAT IT COMPARES. The running app and proxy containers are found by their compose labels - no
# compose file is read, so nothing an operator can edit decides what this root timer runs. Each one
# names the image it was started from, say registry.example.net/homespool, which is the tag the
# deployment follows (latest when none is written). The registry's current digest for that tag
# against the running image's is the whole of "is there something newer". When there is:
#
#   - Homespool's commits between the running revision and the published one, counted by type,
#     merges left out. It asks GitHub for the history of the PUBLISHED revision - which every
#     deployment following that tag asks for - and finds its own revision in the answer, so the one
#     thing that would identify this deployment's build, and with it its known defects, never leaves
#     the machine.
#   - The .NET runtime, when the published image carries a newer one: every release in between, from
#     Microsoft's release metadata, and whether it was a security release.
#   - Whether the published image is built on a different base. A newer image on the same revision
#     is a rebuild, and a rebuild is published for what it fixes underneath.
#
# A running revision only counts when the running image's source label names the same repository as
# the published one. An image built before Homespool labelled itself inherits its base's labels, and
# nginx's commit is not a place in Homespool's history.
#
# NEEDS NO CREDENTIALS, and must not: it runs as root, and root's Docker login is nobody's. The
# registry has to allow anonymous reads of these images - GHCR's public packages do. An image with
# no registry in its name was built on this machine, has nothing published to compare with, and is
# reported as such without any request leaving it.
#
# A SOURCE THAT CANNOT BE REACHED FAILS THE RUN, and the previous report stays: a report written from
# part of the evidence would say "nothing to take" for the part it could not see.
#
# HOMESPOOL_PROJECT is the compose project, set by the unit. Needs docker with buildx, curl and jq.
set -eu

project="${HOMESPOOL_PROJECT:-homespool}"
state_dir="${STATE_DIRECTORY:-/var/lib/homespool}"
state_file="$state_dir/update-check.json"
dotnet_index="https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json"

die() {
    echo "homespool-update-check: $*" >&2
    exit 1
}

# Never piped: dash has no pipefail, so `fetch | jq` would report jq's success after curl had failed,
# and the report would be written without the part it could not fetch. Into a file, then jq.
fetch() {
    curl -fsSL --max-time 60 "$1" || die "could not fetch $1"
}

for tool in docker curl jq; do
    command -v "$tool" >/dev/null 2>&1 || die "$tool is needed and is not on PATH"
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT INT TERM

platform="$(docker version --format '{{.Server.Os}}/{{.Server.Arch}}')"
checked="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

for service in homespool proxy; do
    entry="$work/$service.json"
    container="$(docker ps --filter "label=com.docker.compose.project=$project" \
        --filter "label=com.docker.compose.service=$service" --format '{{.ID}}' | head -n 1)"

    if [ -z "$container" ]; then
        jq -n --arg s "$service" '{service: $s, status: "not-running"}' > "$entry"
        echo "$service: not running"
        continue
    fi

    reference="$(docker inspect --format '{{.Config.Image}}' "$container")"
    image_id="$(docker inspect --format '{{.Image}}' "$container")"

    case "$reference" in
        *@*)
            jq -n --arg s "$service" --arg r "$reference" '{service: $s, reference: $r, status: "pinned"}' > "$entry"
            echo "$service: pinned to a digest ($reference), so there is no tag to follow"
            continue
            ;;
    esac

    # Docker's own rule: the first component names a registry only if it has a dot or a port, or
    # is localhost. Anything else was built here, or would be Docker Hub's library, which is not us.
    case "$reference" in
        */*) first="${reference%%/*}" ;;
        *) first="" ;;
    esac
    case "$first" in
        *.* | *:* | localhost) ;;
        *)
            jq -n --arg s "$service" --arg r "$reference" '{service: $s, reference: $r, status: "local"}' > "$entry"
            echo "$service: built on this machine ($reference), so nothing is published to compare with"
            continue
            ;;
    esac

    case "${reference##*/}" in
        *:*) ;;
        *) reference="$reference:latest" ;;
    esac
    repository="${reference%:*}"

    published_digest="$(docker buildx imagetools inspect --format '{{.Manifest.Digest}}' "$reference")" ||
        die "could not read $reference from its registry"
    docker buildx imagetools inspect --format '{{json .Image}}' "$reference" > "$work/$service.published.json" ||
        die "could not read $reference's configuration from its registry"
    docker image inspect --format '{{json .}}' "$image_id" > "$work/$service.running.json"

    # One platform's configuration: a single manifest answers with it directly, an index with one per
    # platform, and this machine runs its own.
    jq --arg p "$platform" 'if has("config") then . else (.[$p] // (to_entries[0].value)) end' \
        "$work/$service.published.json" > "$work/$service.published.config.json"

    if jq -e --arg d "$repository@$published_digest" '(.RepoDigests // []) | any(. == $d)' \
            "$work/$service.running.json" >/dev/null; then
        jq -n --arg s "$service" --arg r "$reference" --arg d "$published_digest" \
            '{service: $s, reference: $r, status: "current", digest: $d}' > "$entry"
        echo "$service: current ($reference is $published_digest)"
        continue
    fi

    label() { jq -r --arg k "$1" '(.config.Labels // .Config.Labels // {})[$k] // ""' "$2"; }
    envvar() { jq -r --arg k "$1=" '((.config.Env // .Config.Env // [])[] | select(startswith($k)) | ltrimstr($k)) // ""' "$2"; }

    pub="$work/$service.published.config.json"
    run="$work/$service.running.json"
    published_revision="$(label org.opencontainers.image.revision "$pub")"
    published_source="$(label org.opencontainers.image.source "$pub")"
    running_revision="$(label org.opencontainers.image.revision "$run")"
    running_source="$(label org.opencontainers.image.source "$run")"
    [ "$running_source" = "$published_source" ] || running_revision=""

    # --- Homespool, from the published revision's history ---
    printf 'null\n' > "$work/$service.homespool.json"
    slug="${published_source#https://github.com/}"
    if [ -n "$published_revision" ] && [ -n "$running_revision" ] && [ "$slug" != "$published_source" ]; then
        if [ "$published_revision" = "$running_revision" ]; then
            printf '{"commits":0,"by_type":{},"found":true}\n' > "$work/$service.homespool.json"
        else
            # What the published revision has and the running one does not: everything the fetched
            # history reaches from the published revision, less everything it reaches from the running
            # one, walked along the parent links. Not the list's order, which is by date and puts a
            # branch merged late among commits long older than it.
            fetch "https://api.github.com/repos/$slug/commits?sha=$published_revision&per_page=100" \
                > "$work/$service.commits.json"
            jq --arg running "$running_revision" '
                    (map({key: .sha, value: [.parents[].sha]}) | from_entries) as $parents
                    | ({seen: {}, queue: [$running]}
                       | until(.queue | length == 0;
                             .queue[0] as $c
                             | .queue |= .[1:]
                             | if .seen[$c] or ($parents[$c] == null) then .
                               else .seen[$c] = true | .queue += $parents[$c] end)
                       | .seen) as $behind
                    | map(select(($behind[.sha] // false) | not))
                    | map(select((.parents | length) == 1) | .commit.message | split("\n")[0])
                    | {
                        commits: length,
                        by_type: (map(capture("^(?<type>[a-z]+)(\\([^)]*\\))?!?:").type // "other")
                                  | group_by(.) | map({key: .[0], value: length}) | from_entries),
                        found: ($parents[$running] != null)
                    }' "$work/$service.commits.json" > "$work/$service.homespool.json"
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

    jq -n --arg s "$service" --arg r "$reference" --arg d "$published_digest" \
        --arg pr "$published_revision" --arg rr "$running_revision" \
        --arg pb "$(label org.opencontainers.image.base.digest "$pub")" \
        --arg rb "$(label org.opencontainers.image.base.digest "$run")" \
        --slurpfile homespool "$work/$service.homespool.json" \
        --slurpfile runtime "$work/$service.runtime.json" '
        ($homespool[0]) as $h | ($runtime[0]) as $rt
        | {
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
                (if $h != null and ($h.by_type.fix // 0) > 0 then "\($h.by_type.fix) Homespool fixes" else empty end),
                (if $h != null and ($h.found | not) then "more Homespool changes than the history reaches" else empty end),
                (if $rt != null then ($rt.releases[] | select(.security) | ".NET \(.version), a security release") else empty end),
                (if $pr != "" and $pr == $rr then "rebuilt from the same revision on newer packages" else empty end),
                (if $pb != "" and $rb != "" and $rb != $pb and $pr != $rr then "built on a newer base" else empty end)
            ]
        }' > "$entry"

    echo "$service: newer image published ($reference is $published_digest): $(jq -r '.reasons | if length == 0 then "nothing it names" else join("; ") end' "$entry")"
done

jq -s --arg checked "$checked" \
    '{schema: 1, checked: $checked, update_available: any(.[]; .status == "newer"), services: .}' \
    "$work/homespool.json" "$work/proxy.json" > "$work/update-check.json"

mkdir -p "$state_dir"
cp "$work/update-check.json" "$state_dir/.update-check.json.new"
mv -f "$state_dir/.update-check.json.new" "$state_file"
