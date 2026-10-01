/*
 * The screen a panel shows before the host has said anything, and whenever the link drops.
 *
 * It names the panel by the last six characters of its hardware id: enough to match a
 * panel against a settings entry, short enough to read across a cockpit (spec §6.2). That
 * is how you tell five identical black discs apart without unplugging them one at a time.
 */

#include <stdio.h>
#include <string.h>

#include "ui_view.h"

static lv_obj_t *s_root;
static lv_obj_t *s_id;

lv_obj_t *waiting_create(lv_obj_t *screen)
{
    s_root = ui_container(screen);
    lv_obj_set_flex_flow(s_root, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_flex_align(s_root, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    lv_obj_set_style_pad_row(s_root, 14, 0);

    lv_obj_t *name = lv_label_create(s_root);
    lv_obj_set_style_text_font(name, &lv_font_montserrat_28, 0);
    lv_obj_set_style_text_color(name, UI_COLOUR_VALUE, 0);
    lv_obj_set_style_text_letter_space(name, 2, 0);
    lv_label_set_text(name, "CROWSNEST");

    lv_obj_t *box = ui_container(s_root);
    lv_obj_set_style_border_color(box, UI_COLOUR_DIM, 0);
    lv_obj_set_style_border_width(box, 2, 0);
    lv_obj_set_style_radius(box, 3, 0);
    lv_obj_set_style_pad_ver(box, 2, 0);
    lv_obj_set_style_pad_hor(box, 14, 0);

    s_id = lv_label_create(box);
    lv_obj_set_style_text_font(s_id, &lv_font_montserrat_28, 0);
    lv_obj_set_style_text_color(s_id, UI_COLOUR_VALUE, 0);
    lv_label_set_text(s_id, "");

    lv_obj_t *status = lv_label_create(s_root);
    lv_obj_set_style_text_font(status, &lv_font_montserrat_14, 0);
    lv_obj_set_style_text_color(status, UI_COLOUR_LABEL, 0);
    lv_label_set_text(status, "waiting for Crowsnest");

    lv_obj_set_hidden(s_root, true);
    return s_root;
}

void waiting_render(const char *hardware_id)
{
    size_t id_len = hardware_id != NULL ? strlen(hardware_id) : 0;
    const char *tail = id_len > 6 ? hardware_id + id_len - 6 : (hardware_id != NULL ? hardware_id : "??????");
    lv_label_set_text_fmt(s_id, "PANEL %s", tail);
}
