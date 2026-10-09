/*
 * One value in the P180 frame: the transponder's code, and later the autopilot's single
 * values. The value is green like the P180's, large since it has the frame to itself, and
 * spaced out so the cursor's underline sits clearly under one digit. It turns amber while it
 * waits for the sim to confirm a write. Labels are not drawn; the title says what it is.
 */

#include "ui_view.h"

/* Montserrat Medium at 72 px, digits and number punctuation only (fonts/ui_font_value_72.c);
 * anything else falls back to the 48 px Montserrat. */
LV_FONT_DECLARE(ui_font_value_72);

#define VALUE_LETTER_SPACE 10

static ui_frame_t s_frame;
static ui_value_t s_value;

lv_obj_t *framed_value_create(lv_obj_t *screen)
{
    s_frame = ui_frame_create(screen);
    s_value = ui_value_create(s_frame.frame, &ui_font_value_72);
    ui_value_set_letter_space(&s_value, VALUE_LETTER_SPACE);

    lv_obj_set_hidden(s_frame.root, true);
    return s_frame.root;
}

void framed_value_render(const cn_state_t *state)
{
    lv_label_set_text(s_frame.title, state->page_title);

    if (state->field_count == 0) {
        lv_obj_set_hidden(s_value.row, true);
        return;
    }

    /* The first field is the one the knob tunes (spec §6.1); a framed page has only that. */
    const cn_field_t *field = &state->fields[0];
    ui_value_show(&s_value, field, field->pending ? UI_COLOUR_PENDING : UI_COLOUR_ACTIVE);
    lv_obj_set_hidden(s_value.row, false);
}
