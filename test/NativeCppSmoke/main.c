#include <stdio.h>
#include <openslide.h>

int main(int argc, char **argv)
{
    if (argc < 2) {
        fprintf(stderr, "usage: %s <slide>\n", argv[0]);
        return 2;
    }

    printf("version=%s\n", openslide_get_version());

    openslide_t *osr = openslide_open(argv[1]);
    if (osr == NULL) {
        fprintf(stderr, "openslide_open failed for %s\n", argv[1]);
        return 2;
    }
    const char *err = openslide_get_error(osr);
    if (err != NULL) {
        fprintf(stderr, "openslide error: %s\n", err);
        openslide_close(osr);
        return 2;
    }

    int64_t w = 0, h = 0;
    openslide_get_level0_dimensions(osr, &w, &h);
    printf("size=%lldx%lld\n", (long long)w, (long long)h);

    openslide_close(osr);
    return 0;
}
