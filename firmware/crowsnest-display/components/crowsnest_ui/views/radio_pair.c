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

#define VALUE_GAP 18

#define VALUE_FONT (&lv_font_montserrat_48)

static ui_frame_t s_frame;
static lv_obj_t *s_active;
static lv_obj_t *s_standby_box;
static ui_value_t s_standby;

lv_obj_t *radio_pair_create(lv_obj_t *screen)
{
    s_frame = ui_frame_create(screen);
    lv_obj_t *frame = s_frame.frame;
    lv_obj_set_style_pad_row(frame, VALUE_GAP, 0);

    s_active = lv_label_create(frame);
    lv_obj_set_style_text_font(s_active, VALUE_FONT, 0);
    lv_obj_set_style_text_color(s_active, UI_COLOUR_ACTIVE, 0);
    lv_label_set_text(s_active, "");

    s_standby_box = ui_container(frame);
    lv_obj_set_style_border_color(s_standby_box, UI_COLOUR_VALUE, 0);
    lv_obj_set_style_border_width(s_standby_box, UI_FRAME_BORDER, 0);
    lv_obj_set_style_radius(s_standby_box, 3, 0);
    lv_obj_set_style_pad_ver(s_standby_box, 2, 0);
    lv_obj_set_style_pad_hor(s_standby_box, 16, 0);

    s_standby = ui_value_create(s_standby_box, VALUE_FONT);

    lv_obj_set_hidden(s_frame.root, true);
    return s_frame.root;
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
    lv_label_set_text(s_frame.title, state->page_title);

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
