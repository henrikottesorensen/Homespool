// The oracle for Homespool's littlefs reader (Homespool.Host/Firmware/LittlefsImage.cs), built
// against the littlefs 2.8 that Buddy 6.5.3 vendors - see generate.sh, which builds and runs it.
//   lfstool make <image> <block_size> <block_count> <source_dir>   format and copy a tree in, as mklittlefs does
//   lfstool hash <image> <block_size> <block_count>                print Prusa's content hash, as mklittlefs computes it
//   lfstool script <image> <block_size> <block_count> <ops...>      w:<path>=<len>:<seed> (write)  d:<path> (mkdir)  r:<path> (remove)
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <dirent.h>
#include <sys/stat.h>
#include "lfs.h"
#include <CommonCrypto/CommonDigest.h>

static FILE *img;
static int bd_read(const struct lfs_config *c, lfs_block_t b, lfs_off_t off, void *buf, lfs_size_t size) {
    fseek(img, (long)b * c->block_size + off, SEEK_SET); return fread(buf, 1, size, img) == size ? 0 : LFS_ERR_IO; }
static int bd_prog(const struct lfs_config *c, lfs_block_t b, lfs_off_t off, const void *buf, lfs_size_t size) {
    fseek(img, (long)b * c->block_size + off, SEEK_SET); return fwrite(buf, 1, size, img) == size ? 0 : LFS_ERR_IO; }
static int bd_erase(const struct lfs_config *c, lfs_block_t b) {
    unsigned char *ff = malloc(c->block_size); memset(ff, 0xff, c->block_size);
    fseek(img, (long)b * c->block_size, SEEK_SET); fwrite(ff, 1, c->block_size, img); free(ff); return 0; }
static int bd_sync(const struct lfs_config *c) { (void)c; return 0; }

static lfs_t lfs; static struct lfs_config cfg;
static void setup(const char *path, int bs, int bc, int create) {
    if (create) { img = fopen(path, "w+b"); unsigned char *ff = malloc(bs); memset(ff, 0xff, bs); for (int i = 0; i < bc; i++) fwrite(ff, 1, bs, img); free(ff); }
    else img = fopen(path, "r+b");
    if (!img) { perror(path); exit(2); }
    memset(&cfg, 0, sizeof cfg);
    cfg.read = bd_read; cfg.prog = bd_prog; cfg.erase = bd_erase; cfg.sync = bd_sync;
    cfg.read_size = 1; cfg.prog_size = 1; cfg.block_size = bs; cfg.block_count = bc;
    cfg.cache_size = bs; cfg.lookahead_size = 16; cfg.block_cycles = 500;
    if (create && lfs_format(&lfs, &cfg)) { fprintf(stderr, "format failed\n"); exit(2); }
    if (lfs_mount(&lfs, &cfg)) { fprintf(stderr, "mount failed\n"); exit(2); }
}

static void copy_tree(const char *src, const char *dst) {
    DIR *d = opendir(src); struct dirent *e; char s[1024], t[1024];
    while ((e = readdir(d))) {
        if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;
        snprintf(s, sizeof s, "%s/%s", src, e->d_name); snprintf(t, sizeof t, "%s/%s", strcmp(dst, "/") ? dst : "", e->d_name);
        struct stat st; stat(s, &st);
        if (S_ISDIR(st.st_mode)) { lfs_mkdir(&lfs, t); copy_tree(s, t); }
        else { FILE *f = fopen(s, "rb"); lfs_file_t lf; lfs_file_open(&lfs, &lf, t, LFS_O_WRONLY | LFS_O_CREAT | LFS_O_TRUNC);
               char buf[4096]; size_t n; while ((n = fread(buf, 1, sizeof buf, f)) > 0) lfs_file_write(&lfs, &lf, buf, n);
               lfs_file_close(&lfs, &lf); fclose(f); }
    }
    closedir(d);
}

static CC_SHA256_CTX sha; static uint32_t counter;
static void mark(void) { CC_SHA256_Update(&sha, (unsigned char *)&counter, 4); counter++; }
static void hash_file(const char *path) {
    mark(); CC_SHA256_Update(&sha, (const unsigned char *)path, strlen(path)); mark();
    lfs_file_t f; lfs_file_open(&lfs, &f, path, LFS_O_RDONLY); unsigned char buf[512]; lfs_ssize_t n;
    while ((n = lfs_file_read(&lfs, &f, buf, sizeof buf)) > 0) CC_SHA256_Update(&sha, buf, n);
    lfs_file_close(&lfs, &f); mark();
}
static void hash_dir(const char *path) {
    mark(); CC_SHA256_Update(&sha, (const unsigned char *)path, strlen(path)); mark();
    lfs_dir_t d; struct lfs_info info; char names[256][256]; int types[256]; int n = 0;
    lfs_dir_open(&lfs, &d, path);
    while (lfs_dir_read(&lfs, &d, &info) > 0) { if (!strcmp(info.name, ".") || !strcmp(info.name, "..")) continue; strcpy(names[n], info.name); types[n++] = info.type; }
    lfs_dir_close(&lfs, &d);
    for (int i = 0; i < n; i++) { char child[1024]; snprintf(child, sizeof child, "%s/%s", strcmp(path, "/") ? path : "", names[i]);
        if (types[i] == LFS_TYPE_DIR) hash_dir(child); else hash_file(child); }
    mark();
}

int main(int argc, char **argv) {
    if (argc < 5) { fprintf(stderr, "usage\n"); return 2; }
    int bs = atoi(argv[3]), bc = atoi(argv[4]);
    if (!strcmp(argv[1], "make")) { setup(argv[2], bs, bc, 1); copy_tree(argv[5], "/"); lfs_unmount(&lfs); }
    else if (!strcmp(argv[1], "script")) {
        setup(argv[2], bs, bc, 1);
        for (int i = 5; i < argc; i++) {
            char *op = argv[i];
            if (op[0] == 'd') lfs_mkdir(&lfs, op + 2);
            else if (op[0] == 'r') lfs_remove(&lfs, op + 2);
            else if (op[0] == 'w') { char path[512]; int len, seed; char *eq = strchr(op, '='); memcpy(path, op + 2, eq - op - 2); path[eq - op - 2] = 0;
                sscanf(eq + 1, "%d:%d", &len, &seed); lfs_file_t f; lfs_file_open(&lfs, &f, path, LFS_O_WRONLY | LFS_O_CREAT | LFS_O_TRUNC);
                for (int k = 0; k < len; k++) { unsigned char c = (unsigned char)(k * 31 + seed); lfs_file_write(&lfs, &f, &c, 1); } lfs_file_close(&lfs, &f); }
        }
        lfs_unmount(&lfs);
    }
    else if (!strcmp(argv[1], "hash")) {
        setup(argv[2], bs, bc, 0); CC_SHA256_Init(&sha); counter = 0;
        hash_dir("/"); unsigned char out[32]; CC_SHA256_Final(out, &sha);
        for (int i = 0; i < 32; i++) printf("%02x", out[i]); printf("\n");
    }
    return 0;
}
