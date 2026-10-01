/*
 * Gestures: the knob button's short and long presses, and taps on the screen.
 *
 * Pure on purpose: each step function takes what the hardware reads now and the time in
 * milliseconds, and says whether a gesture just happened. No FreeRTOS, no I/O, so the rules
 * can run under Unity on the IDF linux target (spec §11.1). The caller reads the hardware
 * and sends the events.
 */

#pragma once

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Held this long, a press is long, and is reported the moment it qualifies. */
#define GESTURE_LONG_PRESS_MS 600

/* A tap is a short touch that stays put. Anything longer or further is left alone, so a
 * swipe or a resting finger never swaps a radio. */
#define GESTURE_TAP_MAX_MS 400
#define GESTURE_TAP_MAX_TRAVEL_PX 40

typedef enum {
    BUTTON_EVENT_NONE = 0,
    BUTTON_EVENT_SHORT,
    BUTTON_EVENT_LONG,
} button_event_t;

typedef struct {
    bool     was_pressed;
    bool     long_sent;
    uint32_t pressed_at_ms;
} button_tracker_t;

/*
 * One poll of the button. A long press is reported while still held, as soon as it
 * qualifies, so the knob feels like it responded when the pilot expected; its release then
 * reports nothing. A short press is reported on release.
 */
button_event_t button_step(button_tracker_t *tracker, bool pressed, uint32_t now_ms);

typedef struct {
    bool     down;
    bool     moved;
    uint32_t since_ms;
    int      x;
    int      y;
} touch_tracker_t;

/*
 * One touch sample. Returns true when a tap has just ended, with where it started in
 * *tap_x, *tap_y. A tap is reported on lift, not on contact: only then is it known not to
 * be a swipe. Skip the call for a sample that failed to read, rather than passing
 * down = false, or one long press becomes a tap.
 */
bool touch_step(touch_tracker_t *tracker, bool down, int x, int y, uint32_t now_ms, int *tap_x, int *tap_y);

#ifdef __cplusplus
}
#endif
