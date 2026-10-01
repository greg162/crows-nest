#include "gestures.h"

#include <stdlib.h>

button_event_t button_step(button_tracker_t *tracker, bool pressed, uint32_t now_ms)
{
    button_event_t event = BUTTON_EVENT_NONE;

    if (pressed && !tracker->was_pressed) {
        tracker->pressed_at_ms = now_ms;
        tracker->long_sent = false;
    } else if (pressed && !tracker->long_sent && now_ms - tracker->pressed_at_ms >= GESTURE_LONG_PRESS_MS) {
        tracker->long_sent = true;
        event = BUTTON_EVENT_LONG;
    } else if (!pressed && tracker->was_pressed && !tracker->long_sent) {
        event = BUTTON_EVENT_SHORT;
    }

    tracker->was_pressed = pressed;
    return event;
}

bool touch_step(touch_tracker_t *tracker, bool down, int x, int y, uint32_t now_ms, int *tap_x, int *tap_y)
{
    if (down && !tracker->down) {
        *tracker = (touch_tracker_t){ .down = true, .since_ms = now_ms, .x = x, .y = y };
        return false;
    }

    if (down) {
        if (abs(x - tracker->x) > GESTURE_TAP_MAX_TRAVEL_PX || abs(y - tracker->y) > GESTURE_TAP_MAX_TRAVEL_PX) {
            tracker->moved = true;
        }
        return false;
    }

    if (!tracker->down) {
        return false;
    }

    tracker->down = false;
    if (tracker->moved || now_ms - tracker->since_ms > GESTURE_TAP_MAX_MS) {
        return false;
    }

    *tap_x = tracker->x;
    *tap_y = tracker->y;
    return true;
}
