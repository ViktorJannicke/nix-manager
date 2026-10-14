// Long-lived wlr virtual pointer for the Hyprland sandbox.
//
// One-shot tools such as wlrctl create a pointer device per call. The seat
// then gains and loses its pointer capability around every command, and GTK
// clients such as Waybar release their wl_pointer before the button arrives.
// This keeps one device for the whole session and reads commands from stdin:
//
//   move X Y        absolute position in layout pixels (needs `extent`)
//   extent W H      layout size used for absolute motion
//   down|up|click left|right|middle
//   scroll DY       vertical scroll in wheel steps
//
// Each command is applied with a frame and a roundtrip; "ok" or "error ..."
// is printed per line so callers can wait for it.
#define _POSIX_C_SOURCE 200809L
#include <linux/input-event-codes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <wayland-client.h>

#include "wlr-virtual-pointer-unstable-v1-client-protocol.h"

static struct wl_seat *seat;
static struct zwlr_virtual_pointer_manager_v1 *manager;

static void global(void *data, struct wl_registry *registry, uint32_t name, const char *interface, uint32_t version) {
    if (strcmp(interface, wl_seat_interface.name) == 0 && !seat)
        seat = wl_registry_bind(registry, name, &wl_seat_interface, 1);
    else if (strcmp(interface, zwlr_virtual_pointer_manager_v1_interface.name) == 0)
        manager = wl_registry_bind(registry, name, &zwlr_virtual_pointer_manager_v1_interface, 1);
}

static void global_remove(void *data, struct wl_registry *registry, uint32_t name) {}

static const struct wl_registry_listener registry_listener = {global, global_remove};

static uint32_t now_ms(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint32_t)(ts.tv_sec * 1000 + ts.tv_nsec / 1000000);
}

static int button_code(const char *name) {
    if (strcmp(name, "left") == 0) return BTN_LEFT;
    if (strcmp(name, "right") == 0) return BTN_RIGHT;
    if (strcmp(name, "middle") == 0) return BTN_MIDDLE;
    return -1;
}

int main(void) {
    struct wl_display *display = wl_display_connect(NULL);
    if (!display) {
        fprintf(stderr, "vpointer: cannot connect to WAYLAND_DISPLAY\n");
        return 1;
    }
    struct wl_registry *registry = wl_display_get_registry(display);
    wl_registry_add_listener(registry, &registry_listener, NULL);
    wl_display_roundtrip(display);
    if (!manager) {
        fprintf(stderr, "vpointer: compositor lacks zwlr_virtual_pointer_manager_v1\n");
        return 1;
    }
    struct zwlr_virtual_pointer_v1 *pointer = zwlr_virtual_pointer_manager_v1_create_virtual_pointer(manager, seat);
    wl_display_roundtrip(display);

    uint32_t width = 1280, height = 720;
    char line[256];
    setvbuf(stdout, NULL, _IOLBF, 0);
    while (fgets(line, sizeof line, stdin)) {
        char cmd[32] = "", arg[32] = "";
        int a = 0, b = 0;
        if (sscanf(line, "%31s", cmd) != 1) continue;
        if (strcmp(cmd, "extent") == 0 && sscanf(line, "%*s %d %d", &a, &b) == 2 && a > 0 && b > 0) {
            width = (uint32_t)a;
            height = (uint32_t)b;
        } else if (strcmp(cmd, "move") == 0 && sscanf(line, "%*s %d %d", &a, &b) == 2 && a >= 0 && b >= 0) {
            zwlr_virtual_pointer_v1_motion_absolute(pointer, now_ms(), (uint32_t)a, (uint32_t)b, width, height);
            zwlr_virtual_pointer_v1_frame(pointer);
        } else if ((strcmp(cmd, "down") == 0 || strcmp(cmd, "up") == 0 || strcmp(cmd, "click") == 0) &&
                   sscanf(line, "%*s %31s", arg) == 1 && button_code(arg) >= 0) {
            int code = button_code(arg);
            if (strcmp(cmd, "up") != 0) {
                zwlr_virtual_pointer_v1_button(pointer, now_ms(), code, WL_POINTER_BUTTON_STATE_PRESSED);
                zwlr_virtual_pointer_v1_frame(pointer);
            }
            if (strcmp(cmd, "down") != 0) {
                if (strcmp(cmd, "click") == 0) wl_display_roundtrip(display);
                zwlr_virtual_pointer_v1_button(pointer, now_ms(), code, WL_POINTER_BUTTON_STATE_RELEASED);
                zwlr_virtual_pointer_v1_frame(pointer);
            }
        } else if (strcmp(cmd, "scroll") == 0 && sscanf(line, "%*s %d", &a) == 1) {
            zwlr_virtual_pointer_v1_axis_source(pointer, WL_POINTER_AXIS_SOURCE_WHEEL);
            zwlr_virtual_pointer_v1_axis_discrete(pointer, now_ms(), WL_POINTER_AXIS_VERTICAL_SCROLL,
                                                  wl_fixed_from_int(15 * a), a);
            zwlr_virtual_pointer_v1_frame(pointer);
        } else {
            printf("error unknown command: %s", line);
            continue;
        }
        if (wl_display_roundtrip(display) < 0) {
            printf("error compositor connection lost\n");
            return 1;
        }
        printf("ok\n");
    }
    zwlr_virtual_pointer_v1_destroy(pointer);
    wl_display_roundtrip(display);
    wl_display_disconnect(display);
    return 0;
}
