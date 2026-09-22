/*
 * crowsnest_input — the encoder and button shim (spec §9.2).
 *
 * This is THE per-board file. ESP-BSP's display, touch and button APIs are well
 * established, but encoder support is the least uniform part across BSPs and it is the one
 * peripheral this project cannot do without. So Crowsnest defines a deliberately tiny
 * surface: a board port implements these three functions and everything above is
 * identical. On the M5Dial they would be backed by that board's equivalents.
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

#ifdef __cplusplus
}
#endif
