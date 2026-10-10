/*
 * One value in the P180 frame: the transponder's code, and later the autopilot's single
 * values. The value is green like the P180's, large since it has the frame to itself, and
 * spaced out so the cursor's underline sits clearly under one digit. It turns amber while it
 * waits for the sim to confirm a write. Labels are not drawn; the title says what it is.
 *
 * Under the frame, a small caption from the host, such as the transponder's mode: light
 * grey, or white while lit (IDENT). Its line is kept when it is empty, so the frame does
 * not move as a caption comes and goes.
 */

#include "ui_view.h"

/* Montserrat Medium at 72 px, digits and number punctuation only (fonts/ui_font_value_72.c);
 * anything else falls back to the 48 px Montserrat. */
LV_FONT_DECLARE(ui_font_value_72);

#define VALUE_LETTER_SPACE 10

#define CAPTION_FONT (&lv_font_montserrat_14)
#define CAPTION_GAP 8

static ui_frame_t s_frame;
static ui_value_t s_value;
static lv_obj_t *s_caption;

lv_obj_t *framed_value_create(lv_obj_t *screen)
{
    s_frame = ui_frame_create(screen);
    s_value = ui_value_create(s_frame.frame, &ui_font_value_72);
    ui_value_set_letter_space(&s_value, VALUE_LETTER_SPACE);

    /* The root grows by the caption's line; the frame keeps its place at the top of it. */
    int32_t caption_height = CAPTION_GAP + lv_font_get_line_height(CAPTION_FONT);
    lv_obj_set_height(s_frame.root, lv_obj_get_style_height(s_frame.root, 0) + caption_height);

    s_caption = lv_label_create(s_frame.root);
    lv_obj_set_style_text_font(s_caption, CAPTION_FONT, 0);
    lv_obj_set_style_text_letter_space(s_caption, 2, 0);
    lv_obj_align(s_caption, LV_ALIGN_BOTTOM_MID, 0, 0);
    lv_label_set_text(s_caption, "");

    lv_obj_set_hidden(s_frame.root, true);
    return s_frame.root;
}

void framed_value_render(const cn_state_t *state)
{
    lv_label_set_text(s_frame.title, state->page_title);
    lv_label_set_text(s_caption, state->page_caption);
    lv_obj_set_style_text_color(s_caption, state->caption_lit ? UI_COLOUR_VALUE : UI_COLOUR_LABEL, 0);

    if (state->field_count == 0) {
        lv_obj_set_hidden(s_value.row, true);
        return;
    }

    /* The first field is the one the knob tunes (spec §6.1); a framed page has only that. */
    const cn_field_t *field = &state->fields[0];
    ui_value_show(&s_value, field, field->pending ? UI_COLOUR_PENDING : UI_COLOUR_ACTIVE);
    lv_obj_set_hidden(s_value.row, false);
}
