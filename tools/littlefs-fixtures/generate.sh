#!/usr/bin/env bash
#
# Regenerate the littlefs fixtures LittlefsImageTests reads: small images written by the littlefs that
# Buddy 6.5.3 vendors - the version whose printers read the resources images of a .bbf from before
# 6.6 - and the content hash of each, computed the way utils/mklittlefs.py computes it.
#
#   tools/littlefs-fixtures/generate.sh ~/Prusa/Prusa-Firmware-Buddy
#
# Needs a C compiler and macOS's CommonCrypto (lfstool.c's SHA-256). Writes
# Homespool.Host.Test/littlefs/*.img and Homespool.Host.Test/littlefs/fixtures.json.

set -euo pipefail

buddy=${1:?usage: generate.sh <Prusa-Firmware-Buddy checkout>}
here=$(cd "$(dirname "$0")" && pwd)
out=$(cd "$here/../.." && pwd)/Homespool.Host.Test/littlefs
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

for f in lfs.c lfs.h lfs_util.c lfs_util.h; do
    git -C "$buddy" show "v6.5.3:lib/Middlewares/Third_Party/littlefs/$f" > "$work/$f"
done

cc -O1 -w -I"$work" -DLFS_NO_DEBUG -DLFS_NO_WARN -o "$work/lfstool" "$here/lfstool.c" "$work/lfs.c" "$work/lfs_util.c"
mkdir -p "$out"

# A tree: nested directories, files inline and in skip-lists of one block and many, an empty file,
# and a directory with enough entries to split across metadata pairs.
many=()
for i in $(seq -w 0 23); do many+=("w:/many/f$i=20:$i"); done
"$work/lfstool" script "$out/tree.img" 256 96 \
    d:/fonts w:/fonts/a.bin=3000:1 w:/fonts/b.bin=40:2 \
    d:/web d:/web/css w:/web/css/x.css=700:3 w:/web/index.html=200:4 \
    w:/empty=0:0 d:/many "${many[@]}" w:/readme=5:9

# The same few names written, removed and written again, so the answer depends on reading the
# latest commit for each id across creates and deletes.
"$work/lfstool" script "$out/rewritten.img" 256 64 \
    w:/a=100:1 w:/b=2000:2 w:/c=10:3 r:/b w:/a=3000:4 d:/d w:/d/x=50:5 r:/c w:/b=30:6 w:/a=5:7

# The rewritten image with one more commit whose CRC no longer holds: littlefs drops it and reads the
# commit before, as after a write cut off by power loss.
"$work/lfstool" script "$work/one-more.img" 256 64 \
    w:/a=100:1 w:/b=2000:2 w:/c=10:3 r:/b w:/a=3000:4 d:/d w:/d/x=50:5 r:/c w:/b=30:6 w:/a=5:7 w:/t=5:9
python3 -I "$here/tamper.py" torn "$out/rewritten.img" "$work/one-more.img" "$out/torn.img"

# The tree with the newer block of its superblock pair unreadable: littlefs reads the older one.
python3 -I "$here/tamper.py" older-block "$out/tree.img" "$out/older-block.img" 256

# A block size littlefs takes and the printer's 16-byte cache cannot.
"$work/lfstool" script "$out/odd-block-size.img" 200 32 w:/a=10:1 w:/b=300:2

# A name littlefs takes and a path cannot carry unchanged.
"$work/lfstool" script "$out/non-ascii-name.img" 256 32 "w:/caf$(printf '\xc3\xa9')=10:1"

entry() {
    echo "  { \"image\": \"$1\", \"blockSize\": $2, \"blockCount\": $3, \"hash\": \"$("$work/lfstool" hash "$out/$1" "$2" "$3")\" }${4-}"
}

{
    echo '['
    entry tree.img 256 96 ,
    entry rewritten.img 256 64 ,
    entry torn.img 256 64 ,
    entry older-block.img 256 96
    echo ']'
} > "$out/fixtures.json"

cat "$out/fixtures.json"
