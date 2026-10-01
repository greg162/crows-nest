/*
 * The active/standby pair, drawn like the MSFS P180's radio tuning display: a white frame
 * with the page title set into its top border, the active value above in green, and the
 * standby value below in white inside a box that marks it as the one the knob tunes.
 * Both values are the same size, so swapping them does not make the screen jump.
 *
 * Which is which comes from the field roles (spec §6.1): primary is the tuned value, so on
 * a pair page it is standby; secondary is active. Labels are not drawn; the position says
 * it. Either value turns amber while it waits for the sim to confirm a write.
 */

#include "ui_view.h"

#define FRAME_WIDTH 380
#define FRAME_HEIGHT 240
#define FRAME_BORDER 2
#define VALUE_GAP 18

#define TITLE_FONT (&lv_font_montserrat_24)
#define VALUE_FONT (&lv_font_montserrat_48)

static lv_obj_t *s_root;
static lv_obj_t *s_title;
static lv_obj_t *s_active;
static lv_obj_t *s_standby_box;
static ui_value_t s_standby;

lv_obj_t *radio_pair_create(lv_obj_t *screen)
{
    /* The title straddles the frame's top border, so the root is taller than the frame by
     * half a title line and the frame sits at the bottom of it. */
    int32_t title_height = lv_font_get_line_height(TITLE_FONT);
    int32_t frame_top = title_height / 2 - FRAME_BORDER / 2;

    s_root = ui_container(screen);
    lv_obj_set_size(s_root, FRAME_WIDTH, frame_top + FRAME_HEIGHT);

    lv_obj_t *frame = ui_container(s_root);
    lv_obj_set_size(frame, FRAME_WIDTH, FRAME_HEIGHT);
    lv_obj_set_pos(frame, 0, frame_top);
    lv_obj_set_style_border_color(frame, UI_COLOUR_VALUE, 0);
    lv_obj_set_style_border_width(frame, FRAME_BORDER, 0);
    lv_obj_set_style_radius(frame, 4, 0);
    lv_obj_set_flex_flow(frame, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_flex_align(frame, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    lv_obj_set_style_pad_row(frame, VALUE_GAP, 0);

    /* Created after the frame so it draws over the border, on a black ground that cuts
     * the gap the border would otherwise run through. */
    s_title = lv_label_create(s_root);
    lv_obj_set_style_text_font(s_title, TITLE_FONT, 0);
    lv_obj_set_style_text_color(s_title, UI_COLOUR_VALUE, 0);
    lv_obj_set_style_text_letter_space(s_title, 1, 0);
    lv_obj_set_style_bg_color(s_title, UI_COLOUR_BACKGROUND, 0);
    lv_obj_set_style_bg_opa(s_title, LV_OPA_COVER, 0);
    lv_obj_set_style_pad_hor(s_title, 12, 0);
    lv_obj_align(s_title, LV_ALIGN_TOP_MID, 0, 0);
    lv_label_set_text(s_title, "");

    s_active = lv_label_create(frame);
    lv_obj_set_style_text_font(s_active, VALUE_FONT, 0);
    lv_obj_set_style_text_color(s_active, UI_COLOUR_ACTIVE, 0);
    lv_label_set_text(s_active, "");

    s_standby_box = ui_container(frame);
    lv_obj_set_style_border_color(s_standby_box, UI_COLOUR_VALUE, 0);
    lv_obj_set_style_border_width(s_standby_box, FRAME_BORDER, 0);
    lv_obj_set_style_radius(s_standby_box, 3, 0);
    lv_obj_set_style_pad_ver(s_standby_box, 2, 0);
    lv_obj_set_style_pad_hor(s_standby_box, 16, 0);

    s_standby = ui_value_create(s_standby_box, VALUE_FONT);

    lv_obj_set_hidden(s_root, true);
    return s_root;
}

static const cn_field_t *field_with_role(const cn_state_t *state, cn_role_t role)
{
    for (uint8_t i = 0; i < state->field_count; i++) {
        if (state->fields[i].role == role) {
            return &state->fields[i];
        }
    }
    return NULL;
}

void radio_pair_render(const cn_state_t *state)
{
    lv_label_set_text(s_title, state->page_title);

    const cn_field_t *active = field_with_role(state, CN_ROLE_SECONDARY);
    if (active != NULL) {
        lv_label_set_text(s_active, active->text);
        lv_obj_set_style_text_color(s_active, active->pending ? UI_COLOUR_PENDING : UI_COLOUR_ACTIVE, 0);
        lv_obj_set_hidden(s_active, false);
    } else {
        lv_obj_set_hidden(s_active, true);
    }

    const cn_field_t *standby = field_with_role(state, CN_ROLE_PRIMARY);
    if (standby != NULL) {
        ui_value_show(&s_standby, standby, standby->pending ? UI_COLOUR_PENDING : UI_COLOUR_VALUE);
        lv_obj_set_hidden(s_standby_box, false);
    } else {
        lv_obj_set_hidden(s_standby_box, true);
    }
}
