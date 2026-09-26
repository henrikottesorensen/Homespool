#!/usr/bin/env bash
#
# Judges the images the registry serves under latest - is there a fix they lack that matters - and
# publishes the answer beside them, as the artifact homespool-verdict:latest:
#
#   tools/scan-images.sh              # scan, print the verdict, push it
#   tools/scan-images.sh --no-push    # scan and print only
#
# Three sources, because no one of them sees everything:
#
#   - Trivy, for the distribution packages in each image: vulnerabilities rated CRITICAL or HIGH that
#     have a fixed version. Trivy rather than Grype because it rates by the distribution's own
#     severity where there is one, and the threshold is only as good as the rating.
#   - Microsoft's release metadata, for the .NET runtime, which a package scanner does not see at
#     all: every release of the image's channel newer than its ASPNET_VERSION, and whether it was a
#     security release.
#   - GitHub, for Homespool itself: the commits on the default branch since the image's revision
#     label, counted by type. Nothing marks a security fix yet, so every fix counts.
#
# The verdict is relevant when any image has such a vulnerability, a newer runtime release was a
# security release, or Homespool has a fix since the image's revision. A republished base is
# recorded but is not by itself a reason: a newer base is only worth taking for what it fixes.
#
# TRIVY IS PINNED BY DIGEST AND HANDED NOTHING BUT A SAVED IMAGE. Its release channel was hijacked in
# March 2026 - a malicious v0.69.4 binary and image, and v0.69.5 and v0.69.6 on Docker Hub - so it
# runs from a tag and digest checked against its own release, never a floating tag or its GitHub
# Action, and it never sees the registry's login: this script pulls, saves the image to a file, and
# Trivy reads the file. Telemetry and its version check are off.
#
# FAILS, AND PUSHES NOTHING, WHEN ANY SOURCE FAILS. A verdict built from part of the evidence would
# say "nothing relevant" for the part it could not see, so the previous verdict stays, with its older
# timestamp, which is the honest state. The same goes for an image whose revision is not a clean
# commit GitHub knows: publish it with tools/publish-images.sh first.
#
# REGISTRY is asked of compose, as tools/publish-images.sh does, so .env counts. Needs docker, jq,
# curl and oras, and the registry's login in Docker. GITHUB_TOKEN, when set, is sent to GitHub, which
# otherwise allows sixty anonymous requests an hour.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose=(docker compose -f "$repo_root/compose.yaml")
services=(homespool proxy)

# Checked against https://github.com/aquasecurity/trivy/releases/tag/v0.74.0 (2026-08-14), after the
# versions GHSA-69fq-xp46-6x23 lists as compromised. Move tag and digest together.
trivy_image="aquasec/trivy:0.74.0@sha256:62b1e65e8869bc4b4c6aa4fa2b21595256c7c2f6018a9d9ad61caf87187c1969"
trivy_cache="${HOMESPOOL_TRIVY_CACHE:-${XDG_CACHE_HOME:-$HOME/.cache}/homespool-trivy}"
dotnet_index="https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json"
artifact_type="application/vnd.homespool.verdict.v1+json"

push=1
case "${1:-}" in
    "") ;;
    --no-push) push=0 ;;
    *) echo "usage: $0 [--no-push]" >&2; exit 2 ;;
esac

die() {
    echo "$0: $*" >&2
    exit 1
}

say() { echo "==> $*" >&2; }

for tool in docker jq curl oras; do
    command -v "$tool" >/dev/null 2>&1 || die "$tool is needed and is not on PATH"
done

fetch() {
    local url="$1"
    if [ -n "${GITHUB_TOKEN:-}" ] && [ "${url#https://api.github.com/}" != "$url" ]; then
        curl -fsSL -H "Authorization: Bearer $GITHUB_TOKEN" "$url" || die "could not fetch $url"
    else
        curl -fsSL "$url" || die "could not fetch $url"
    fi
}

work="$(mktemp -d "${TMPDIR:-/tmp}/scan-images.XXXXXX")"
trap 'rm -rf "$work"' EXIT

images="$("${compose[@]}" config --images "${services[@]}")"

registry=""
for image in $images; do
    case "$image" in
        */*) registry="${image%/*}" ;;
        *) die "REGISTRY is not set, in the environment or .env - there is nothing published to scan" ;;
    esac
done

generated="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
mkdir -p "$trivy_cache"

for image in $images; do
    repository="${image%:*}"
    name="${repository##*/}"

    digest="$(docker buildx imagetools inspect --format '{{.Manifest.Digest}}' "$image")"
    pinned="$repository@$digest"
    say "$image is $digest"

    docker pull --quiet "$pinned" >/dev/null

    label() {
        docker image inspect --format "{{index .Config.Labels \"$1\"}}" "$pinned"
    }
    revision="$(label org.opencontainers.image.revision)"
    source="$(label org.opencontainers.image.source)"
    base_name="$(label org.opencontainers.image.base.name)"
    base_digest="$(label org.opencontainers.image.base.digest)"
    aspnet="$(docker image inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$pinned" |
        sed -n 's/^ASPNET_VERSION=//p')"

    # --- the distribution's packages ---
    docker save "$pinned" -o "$work/$name.tar"
    docker run --rm -v "$work:/work" -v "$trivy_cache:/root/.cache" "$trivy_image" image \
        --quiet --no-progress --disable-telemetry --skip-version-check \
        --scanners vuln --ignore-unfixed --severity CRITICAL,HIGH \
        --format json --input "/work/$name.tar" --output "/work/$name.trivy.json"
    rm -f "$work/$name.tar"
    [ -s "$work/$name.trivy.json" ] || die "Trivy wrote no report for $name"

    # --- whether the base has been republished since ---
    base_current=""
    if [ -n "$base_name" ]; then
        base_current="$(docker buildx imagetools inspect --format '{{.Manifest.Digest}}' "$base_name")"
    fi

    # --- the .NET runtime, in the application image only ---
    printf 'null\n' > "$work/$name.runtime.json"
    if [ -n "$aspnet" ]; then
        channel="${aspnet%.*}"
        fetch "$dotnet_index" > "$work/dotnet-index.json"
        releases_url="$(jq -r --arg c "$channel" \
            '.["releases-index"][] | select(.["channel-version"] == $c) | .["releases.json"]' \
            "$work/dotnet-index.json")"
        [ -n "$releases_url" ] || die "the .NET release metadata has no channel $channel"
        fetch "$releases_url" > "$work/dotnet-$channel.json"
        jq --arg installed "$aspnet" '
            def version: split(".") | map(tonumber);
            {
                installed: $installed,
                latest: .["latest-runtime"],
                newer: [
                    .releases[]
                    | select(.["release-version"] | test("^[0-9]+\\.[0-9]+\\.[0-9]+$"))
                    | select((.["release-version"] | version) > ($installed | version))
                    | {
                        version: .["release-version"],
                        date: .["release-date"],
                        security: (.security == true),
                        cves: [.["cve-list"][]?["cve-id"]]
                    }
                ]
            }' "$work/dotnet-$channel.json" > "$work/$name.runtime.json"
    fi

    # --- Homespool since the image's revision ---
    case "$revision" in
        *[!0-9a-f]* | "") die "$image has revision '$revision', which is not a clean commit" ;;
    esac
    [ "${#revision}" -eq 40 ] || die "$image has revision '$revision', which is not a full commit"
    slug="${source#https://github.com/}"
    [ "$slug" != "$source" ] && [ -n "$slug" ] || die "$image names no GitHub source ('$source')"

    if [ ! -s "$work/homespool-$revision.json" ]; then
        branch="$(fetch "https://api.github.com/repos/$slug" | jq -r '.default_branch')"
        fetch "https://api.github.com/repos/$slug/compare/$revision...$branch" |
            jq --arg revision "$revision" --arg branch "$branch" '
                [.commits[] | select((.parents | length) == 1) | .commit.message | split("\n")[0]]
                    as $subjects
                | {
                    revision: $revision,
                    compared_with: $branch,
                    status: .status,
                    commits_since: ($subjects | length),
                    truncated: ((.ahead_by // 0) > (.commits | length)),
                    by_type: (
                        $subjects
                        | map(capture("^(?<type>[a-z]+)(\\([^)]*\\))?!?:").type // "other")
                        | group_by(.) | map({key: .[0], value: length}) | from_entries
                    )
                }' > "$work/homespool-$revision.json"
    fi

    jq -n \
        --arg name "$name" --arg reference "$image" --arg digest "$digest" --arg revision "$revision" \
        --arg base_name "$base_name" --arg base_digest "$base_digest" --arg base_current "$base_current" \
        --slurpfile trivy "$work/$name.trivy.json" \
        --slurpfile runtime "$work/$name.runtime.json" \
        --slurpfile homespool "$work/homespool-$revision.json" '
        {
            name: $name,
            reference: $reference,
            digest: $digest,
            revision: $revision,
            base: {
                name: $base_name,
                digest: $base_digest,
                current_digest: $base_current,
                republished: ($base_digest != "" and $base_current != "" and $base_digest != $base_current)
            },
            vulnerabilities: [
                $trivy[0].Results[]?.Vulnerabilities[]?
                | {id: .VulnerabilityID, severity: .Severity, package: .PkgName,
                   installed: .InstalledVersion, fixed: .FixedVersion}
            ] | unique_by(.id, .package),
            runtime: $runtime[0],
            homespool: $homespool[0]
        }' > "$work/$name.image.json"
done

jq -s --arg generated "$generated" --arg scanner "$trivy_image" '
    . as $images
    | [
        ($images[] | select(.vulnerabilities | length > 0)
            | "\(.name): \(.vulnerabilities | length) critical or high vulnerabilities with a fix"),
        ($images[] | select(.runtime != null) | .runtime.newer[] | select(.security)
            | ".NET \(.version) is a security release"),
        ($images | map(.homespool) | unique_by(.revision)[] | select((.by_type.fix // 0) > 0)
            | "Homespool has \(.by_type.fix) fixes since \(.revision[0:12])")
      ] as $reasons
    | {
        schema: 1,
        generated: $generated,
        scanner: $scanner,
        relevant: ($reasons | length > 0),
        reasons: $reasons,
        images: $images
    }' "$work"/*.image.json > "$work/verdict.json"

relevant="$(jq -r '.relevant' "$work/verdict.json")"
jq -r '.reasons[]' "$work/verdict.json" | while read -r reason; do say "$reason"; done
say "relevant: $relevant"

if [ "$push" -eq 1 ]; then
    # From inside the work directory, so the file's recorded title is verdict.json and not a path.
    (cd "$work" && oras push --artifact-type "$artifact_type" \
        --annotation "org.opencontainers.image.created=$generated" \
        "$registry/homespool-verdict:latest" "verdict.json:$artifact_type" >&2)
    say "pushed $registry/homespool-verdict:latest"
fi

cat "$work/verdict.json"
