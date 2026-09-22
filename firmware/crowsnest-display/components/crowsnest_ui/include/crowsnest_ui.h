/*
 * crowsnest_ui — the screen (spec §9.3).
 *
 * Board-independent: it draws into whatever lv_display_t the BSP hands it and never
 * mentions a pin. It implements the three PageLayout variants and nothing else, rendering
 * pre-formatted strings with a character span underlined. It has no concept of
 * frequencies, altitudes or units, which is precisely why adding NAV and the autopilot
 * costs nothing on this side (spec §5.6).
 *
 * Every function here must be called with the LVGL lock held.
 */

#pragma once

#include "crowsnest_link.h"
#include "esp_err.h"
#include "lvgl.h"

#ifdef __cplusplus
extern "C" {
#endif

esp_err_t crowsnest_ui_init(lv_display_t *display);

/* Draws one state frame. Fields beyond what the layout shows are ignored. */
void crowsnest_ui_render(const cn_state_t *state);

/*
 * The screen shown before the host has said anything, and whenever the link drops.
 * Takes the panel's own hardware id, because an unassigned panel is identified by the
 * last six characters of it (spec §6.2) — which is how you tell five identical black
 * discs apart without unplugging them one at a time.
 */
void crowsnest_ui_show_waiting(const char *hardware_id);

/* A banner over the current page, for notices such as "aircraft_rejected". */
void crowsnest_ui_show_notice(const char *kind);

#ifdef __cplusplus
}
#endif
