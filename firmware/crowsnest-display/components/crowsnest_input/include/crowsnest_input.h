/*
 * crowsnest_input — the encoder and button shim (spec §9.2).
 *
 * This is THE per-board file. ESP-BSP's display, touch and button APIs are well
 * established, but encoder support is the least uniform part across BSPs and it is the one
 * peripheral this project cannot do without. So Crowsnest defines a deliberately tiny
 * surface: a board port implements these functions and everything above is identical.
 * A board without touch reports it unavailable. On the M5Dial they would be backed by that board's equivalents.
 */

#pragma once

#include <stdbool.h>

#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

esp_err_t crowsnest_input_init(void);

/* Signed detent count since the last call, consumed on read. */
int crowsnest_encoder_read_detents(void);

bool crowsnest_button_is_pressed(void);

/* False on a board without touch, or when the controller did not answer at init. */
bool crowsnest_touch_available(void);

/*
 * One touch sample in display pixels. *down is whether a finger is on the screen; x and y
 * are only meaningful while it is. An error means no sample, not a lifted finger.
 */
esp_err_t crowsnest_touch_read(bool *down, int *x, int *y);

#ifdef __cplusplus
}
#endif
