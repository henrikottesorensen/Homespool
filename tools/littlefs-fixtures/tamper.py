"""Byte-level edits generate.sh makes to littlefs images littlefs wrote, for the cases it never writes.

  tamper.py torn <before> <after> <out>   <after> with its last commit's CRC broken: one byte of what
                                          changed since <before> flipped, as a write cut off would leave it
  tamper.py older-block <in> <out> <bs>   <in> with the newer block of its superblock pair unreadable
"""
import struct
import sys


def torn(before_path, after_path, out_path):
    before = open(before_path, 'rb').read()
    after = open(after_path, 'rb').read()
    changed = [i for i in range(len(after)) if before[i] != after[i]]
    image = bytearray(after)
    image[changed[len(changed) // 2]] ^= 0x10
    open(out_path, 'wb').write(image)


def older_block(in_path, out_path, block_size):
    image = bytearray(open(in_path, 'rb').read())
    revs = [struct.unpack_from('<I', image, block * block_size)[0] for block in (0, 1)]
    # lfs_scmp: block 1 is newer when its revision is ahead of block 0's in sequence order.
    newer = 1 if 0 < (revs[1] - revs[0]) & 0xffffffff < 0x80000000 else 0
    # A first tag of zero reads as invalid once XORed with 0xffffffff: no commit in that block.
    image[newer * block_size + 4:newer * block_size + 8] = bytes(4)
    open(out_path, 'wb').write(image)


if __name__ == '__main__':
    if sys.argv[1] == 'torn':
        torn(*sys.argv[2:5])
    elif sys.argv[1] == 'older-block':
        older_block(sys.argv[2], sys.argv[3], int(sys.argv[4]))
    else:
        sys.exit(f'unknown edit {sys.argv[1]}')
