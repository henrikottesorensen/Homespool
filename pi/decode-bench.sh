#!/usr/bin/env bash
# Reruns the recorded H.264 decode measurements, so another board yields a third
# data point rather than a third methodology.
#
# The numbers in that file were taken twice with different tooling and the comparison
# survives only because the workloads matched. This script exists so the next board
# does not have to be reconstructed from prose - the transport, the image, the filter
# graph and the durations are the measurement, not incidental detail.
#
# What is deliberately pinned, and why:
#
#   Transport   RTSP over TCP, always. UDP loss silently flatters a decode measurement
#               - dropped frames are frames not decoded - so a board on a worse link
#               would look faster. The Pi 4 runs do not record their transport, which
#               is the one known methodological gap between them and the Pi 5 figures.
#
#   ffmpeg      Pinned by digest rather than :latest. The Pi 5 column was measured with
#               ffmpeg 8.0.1 out of this exact image; a different ffmpeg is a different
#               measurement, and :latest will drift out from under the comparison.
#
#   Container   A throwaway `docker run`, never `exec` into the deployed sidecar, so a
#               measurement never disturbs the running appliance.
#
# Hardware decode is used when the board has it. A Pi 3 and a Pi 4 expose bcm2835-codec
# as /dev/video10 (decode); a Pi 5 has no such node at all - it kept HEVC and lost the
# H.264 block entirely - so on a Pi 5 the hardware column is skipped rather than faked.
#
# On a 1 GB board this competes with the deployed stack for memory. If it is killed
# rather than finishing, stop the stack first and say so alongside the numbers.
#
# Usage: ./decode-bench.sh [rtsp-url]

set -uo pipefail

URL="${1:-rtsp://192.168.13.217/live}"
IMAGE="alexxit/go2rtc@sha256:675c318b23c06fd862a61d262240c9a63436b4050d177ffc68a32710d9e05bae"

# ---------------------------------------------------------------- board identity ----
# Printed with the results so a pasted table is self-describing. A figure without the
# board it came from is the thing this whole exercise is trying to avoid.

model=$(tr -d '\0' < /proc/device-tree/model 2>/dev/null || echo "unknown")
echo "board      : $model"
echo "kernel     : $(uname -r)"
echo "cores      : $(nproc)   page size: $(getconf PAGESIZE)"
echo "memory     : $(awk '/MemTotal/ {printf "%.1f GiB", $2/1048576}' /proc/meminfo)"
echo "url        : $URL"

# ------------------------------------------------------------- hardware detection ----
# By name, not by node number: the numbering is not stable across boards, and guessing
# /dev/video10 on a board that numbers differently would silently measure nothing.

hw_node=""
for n in /sys/class/video4linux/*/name; do
    [ -e "$n" ] || continue
    if grep -qi 'bcm2835-codec-decode' "$n" 2>/dev/null; then
        hw_node="/dev/$(basename "$(dirname "$n")")"
        break
    fi
done

devflags=()
if [ -n "$hw_node" ]; then
    echo "h264 decode: HARDWARE present at $hw_node"
    devflags=(--device "$hw_node")
else
    echo "h264 decode: none (software only - expected on a Pi 5)"
fi
echo

# ------------------------------------------------------------------------ runner ----
# busybox `time` reports "user\t0m 2.07s"; CPU cost is user+sys, and wall time is kept
# so the per-core percentage can be derived rather than assumed.

run() {
    local label="$1" ffargs="$2"
    local out user sys real cpu pct

    out=$(docker run --rm "${devflags[@]}" --entrypoint sh "$IMAGE" \
              -c "time ffmpeg -hide_banner -loglevel error -y -rtsp_transport tcp $ffargs" 2>&1)

    read -r real user sys <<<"$(printf '%s\n' "$out" | awk '
        function sec(m, s) { sub(/m$/, "", m); sub(/s$/, "", s); return m * 60 + s }
        /^real/ { r = sec($2, $3) }
        /^user/ { u = sec($2, $3) }
        /^sys/  { y = sec($2, $3) }
        END { printf "%.2f %.2f %.2f", r, u, y }')"

    cpu=$(awk -v u="$user" -v s="$sys" 'BEGIN { printf "%.2f", u + s }')

    # The percentage is computed into a variable first, deliberately. Written inline as
    # `printf "%.1f", r > 0 ? ...` awk reads the `>` as output redirection to a file
    # named "0" rather than as a comparison, prints nothing, and leaves an empty column
    # that looks like a formatting slip. Caught on the Pi 5 run.
    pct=$(awk -v c="$cpu" -v r="$real" 'BEGIN { p = (r > 0) ? c * 100 / r : 0; printf "%.1f", p }')

    printf '%-38s cpu %7ss   wall %7ss   %6s%% of a core\n' "$label" "$cpu" "$real" "$pct"

    # Anything ffmpeg complained about, once, after the number - a run that failed and
    # reported 0.02 s would otherwise read as a spectacular result. The dts warning from
    # the null muxer is filtered as known-benign: it is an artefact of discarding output,
    # not of decoding, and it fires often enough to bury a real error.
    printf '%s\n' "$out" \
        | grep -viE '^(real|user|sys)' \
        | grep -v 'non monotonically increasing dts' \
        | grep -v '^$' | head -3 | sed 's/^/    ! /'
}

# ------------------------------------------------------------------ experiments ----
# The three recorded rows, in the same order. Row 2 is the one where the
# Pi 4's hardware path was *worse* than its software path: accelerating the decode puts
# frames in DMA buffers that must be copied back out for a software JPEG encoder, and at
# 25 fps that round trip costs more than the decode saved.

echo "--- software ---"
run "decode only, 10 s"              "-i $URL -t 10 -map 0:v:0 -f null -"
run "decode + MJPEG at 25 fps, 10 s" "-i $URL -t 10 -vf fps=25 -c:v mjpeg -q:v 2 -f mjpeg /dev/null"
run "decode + one JPEG every 5 s"    "-i $URL -t 60 -vf fps=1/5 -q:v 2 -f image2 /tmp/f_%03d.jpg"

if [ -n "$hw_node" ]; then
    echo
    echo "--- hardware (h264_v4l2m2m) ---"
    run "decode only, 10 s"              "-c:v h264_v4l2m2m -i $URL -t 10 -map 0:v:0 -f null -"
    run "decode + MJPEG at 25 fps, 10 s" "-c:v h264_v4l2m2m -i $URL -t 10 -vf fps=25 -c:v mjpeg -q:v 2 -f mjpeg /dev/null"
    run "decode + one JPEG every 5 s"    "-c:v h264_v4l2m2m -i $URL -t 60 -vf fps=1/5 -q:v 2 -f image2 /tmp/f_%03d.jpg"
fi

echo
echo "Recorded figures for comparison:"
echo "                                        Pi 4 sw   Pi 4 hw   Pi 5 sw"
echo "  decode only, 10 s                      5.0 s     1.4 s     2.22 s"
echo "  decode + MJPEG at 25 fps, 10 s        18.3 s    36.6 s    11.28 s"
echo "  decode + one JPEG every 5 s            40.9%     10.6%     18.6%"
