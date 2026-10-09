/*
 * What the views share, private to crowsnest_ui.
 *
 * crowsnest_ui.c owns the screen and picks a view per frame; each file under views/ draws
 * one layout from spec §5.5 and nothing else. A view builds its objects once, under a root
 * container the dispatcher shows and hides, so changing page never allocates.
 *
 * Like the rest of crowsnest_ui, everything here runs with the LVGL lock held.
 */

#pragma once

#include "crowsnest_link.h"
#include "lvgl.h"

/* The panel is a 480 px circle. Anything wider than this chord near the top or bottom of
 * the screen runs into the bezel, so content lives inside a centred column. */
#define UI_CONTENT_WIDTH 360

/* The colours of the MSFS P180's radio tuning display, which the pair layout copies. */
#define UI_COLOUR_BACKGROUND lv_color_hex(0x000000)
#define UI_COLOUR_VALUE lv_color_hex(0xF2F2F2)
#define UI_COLOUR_ACTIVE lv_color_hex(0x35E05B)  /* green: the frequency in use */
#define UI_COLOUR_LABEL lv_color_hex(0x8A8A8A)
#define UI_COLOUR_DIM lv_color_hex(0x6E6E6E)
#define UI_COLOUR_PENDING lv_color_hex(0xFFB000) /* amber: written, not yet confirmed */
#define UI_COLOUR_CURSOR lv_color_hex(0x35B7FF)
#define UI_COLOUR_NOTICE lv_color_hex(0xFF5545)

/* An empty container with no style, padding, scrolling or scrollbars of its own. */
lv_obj_t *ui_container(lv_obj_t *parent);

/*
 * The P180 frame (ui_frame.c): `frame` is the bordered box, a centred column to put values
 * in; `title` is set into its top border; `root` holds both, for the dispatcher to show
 * and hide.
 */
#define UI_FRAME_BORDER 2

typedef struct {
    lv_obj_t *root;
    lv_obj_t *frame;
    lv_obj_t *title;
} ui_frame_t;

ui_frame_t ui_frame_create(lv_obj_t *screen);

/*
 * One value with the host's cursor span underlined. Three labels in a row rather than
 * one, so the span carries its own underline without measuring glyph widths: the text
 * before the cursor, the text under it, and the text after. A font change then cannot put
 * the underline under the wrong digits.
 */
typedef struct {
    lv_obj_t *row;
    lv_obj_t *before;
    lv_obj_t *cursor;
    lv_obj_t *after;
} ui_value_t;

ui_value_t ui_value_create(lv_obj_t *parent, const lv_font_t *font);
void ui_value_set_font(ui_value_t *value, const lv_font_t *font);

/* Spaces the characters out, evenly across the cursor's edges too. */
void ui_value_set_letter_space(ui_value_t *value, int32_t space);

/* Shows the field's text in `colour`, underlining its cursor span if it has a valid one. */
void ui_value_show(ui_value_t *value, const cn_field_t *field, lv_color_t colour);

/* ---- Views ---------------------------------------------------------------------------
 * Each returns its root, created hidden. */

/* CN_LAYOUT_PAIR: the P180 frame, active above standby. */
lv_obj_t *radio_pair_create(lv_obj_t *screen);
void radio_pair_render(const cn_state_t *state);

/* CN_LAYOUT_FRAMED: one value in the P180 frame, large and green. */
lv_obj_t *framed_value_create(lv_obj_t *screen);
void framed_value_render(const cn_state_t *state);

/* CN_LAYOUT_SINGLE and CN_LAYOUT_DUAL: labelled values in a column under the title. */
lv_obj_t *value_stack_create(lv_obj_t *screen);
void value_stack_render(const cn_state_t *state);

/* Before the host has spoken, and whenever the link drops. */
lv_obj_t *waiting_create(lv_obj_t *screen);
void waiting_render(const char *hardware_id);
