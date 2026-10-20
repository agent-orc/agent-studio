#define _GNU_SOURCE
#include <dlfcn.h>
#include <fcntl.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>

/* The disposable review runner needs one two-core review slot. The shared
 * agent worker has a 1.85-core quota; only this fixture process sees the
 * synthetic cpu.max file, while its actual CPU use remains cgroup-limited. */
static int is_cpu_max(const char *path) {
    return path && strstr(path, "/sys/fs/cgroup/") && strstr(path, "/cpu.max");
}

static int quota_file(void) {
    int fd = memfd_create("first-run-review-cpu-max", MFD_CLOEXEC);
    if (fd < 0) return fd;
    if (write(fd, "200000 100000\n", 14) != 14 || lseek(fd, 0, SEEK_SET) < 0) {
        close(fd);
        return -1;
    }
    return fd;
}

int open(const char *path, int flags, ...) {
    static int (*real_open)(const char *, int, mode_t);
    if (is_cpu_max(path)) return quota_file();
    if (!real_open) real_open = dlsym(RTLD_NEXT, "open");
    va_list args;
    va_start(args, flags);
    mode_t mode = (flags & O_CREAT) ? va_arg(args, mode_t) : 0;
    va_end(args);
    return real_open(path, flags, mode);
}

int open64(const char *path, int flags, ...) {
    static int (*real_open64)(const char *, int, mode_t);
    if (is_cpu_max(path)) return quota_file();
    if (!real_open64) real_open64 = dlsym(RTLD_NEXT, "open64");
    va_list args;
    va_start(args, flags);
    mode_t mode = (flags & O_CREAT) ? va_arg(args, mode_t) : 0;
    va_end(args);
    return real_open64(path, flags, mode);
}

int openat(int dirfd, const char *path, int flags, ...) {
    static int (*real_openat)(int, const char *, int, mode_t);
    if (is_cpu_max(path)) return quota_file();
    if (!real_openat) real_openat = dlsym(RTLD_NEXT, "openat");
    va_list args;
    va_start(args, flags);
    mode_t mode = (flags & O_CREAT) ? va_arg(args, mode_t) : 0;
    va_end(args);
    return real_openat(dirfd, path, flags, mode);
}

int openat64(int dirfd, const char *path, int flags, ...) {
    static int (*real_openat64)(int, const char *, int, mode_t);
    if (is_cpu_max(path)) return quota_file();
    if (!real_openat64) real_openat64 = dlsym(RTLD_NEXT, "openat64");
    va_list args;
    va_start(args, flags);
    mode_t mode = (flags & O_CREAT) ? va_arg(args, mode_t) : 0;
    va_end(args);
    return real_openat64(dirfd, path, flags, mode);
}
