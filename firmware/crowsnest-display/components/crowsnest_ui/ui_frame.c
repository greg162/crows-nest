/*
 * The MSFS P180's frame, shared by the views that copy its look: a white rounded border
 * with the page title set into the top edge.
 */

#include "ui_view.h"

#define FRAME_WIDTH 380
#define FRAME_HEIGHT 240

#define TITLE_FONT (&lv_font_montserrat_24)

ui_frame_t ui_frame_create(lv_obj_t *screen)
{
    ui_frame_t ui = { 0 };

    /* The title straddles the frame's top border, so the root is taller than the frame by
     * half a title line and the frame sits at the bottom of it. */
    int32_t title_height = lv_font_get_line_height(TITLE_FONT);
    int32_t frame_top = title_height / 2 - UI_FRAME_BORDER / 2;

    ui.root = ui_container(screen);
    lv_obj_set_size(ui.root, FRAME_WIDTH, frame_top + FRAME_HEIGHT);

    ui.frame = ui_container(ui.root);
    lv_obj_set_size(ui.frame, FRAME_WIDTH, FRAME_HEIGHT);
    lv_obj_set_pos(ui.frame, 0, frame_top);
    lv_obj_set_style_border_color(ui.frame, UI_COLOUR_VALUE, 0);
    lv_obj_set_style_border_width(ui.frame, UI_FRAME_BORDER, 0);
    lv_obj_set_style_radius(ui.frame, 4, 0);
    lv_obj_set_flex_flow(ui.frame, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_flex_align(ui.frame, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);

    /* Created after the frame so it draws over the border, on a black ground that cuts
     * the gap the border would otherwise run through. */
    ui.title = lv_label_create(ui.root);
    lv_obj_set_style_text_font(ui.title, TITLE_FONT, 0);
    lv_obj_set_style_text_color(ui.title, UI_COLOUR_VALUE, 0);
    lv_obj_set_style_text_letter_space(ui.title, 1, 0);
    lv_obj_set_style_bg_color(ui.title, UI_COLOUR_BACKGROUND, 0);
    lv_obj_set_style_bg_opa(ui.title, LV_OPA_COVER, 0);
    lv_obj_set_style_pad_hor(ui.title, 12, 0);
    lv_obj_align(ui.title, LV_ALIGN_TOP_MID, 0, 0);
    lv_label_set_text(ui.title, "");

    return ui;
}
