/*
 * The single and dual layouts: the page title, then each field as a small grey label over
 * its value. The primary field gets the big font; on a dual page so does the second, on
 * anything else the rest are secondary furniture. A single page therefore looks like the
 * top of a dual one.
 */

#include "ui_view.h"

typedef struct {
    lv_obj_t  *container;
    lv_obj_t  *label;
    ui_value_t value;
} stack_field_t;

static lv_obj_t *s_root;
static lv_obj_t *s_title;
static stack_field_t s_fields[CN_FIELDS_MAX];

static stack_field_t make_field(lv_obj_t *parent)
{
    stack_field_t field = { 0 };

    field.container = ui_container(parent);
    lv_obj_set_width(field.container, UI_CONTENT_WIDTH);
    lv_obj_set_flex_flow(field.container, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_flex_align(field.container, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    lv_obj_set_style_pad_row(field.container, 2, 0);

    field.label = lv_label_create(field.container);
    lv_obj_set_style_text_font(field.label, &lv_font_montserrat_14, 0);
    lv_obj_set_style_text_color(field.label, UI_COLOUR_LABEL, 0);
    lv_label_set_text(field.label, "");

    field.value = ui_value_create(field.container, &lv_font_montserrat_48);
    return field;
}

lv_obj_t *value_stack_create(lv_obj_t *screen)
{
    s_root = ui_container(screen);
    lv_obj_set_width(s_root, UI_CONTENT_WIDTH);
    lv_obj_set_flex_flow(s_root, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_flex_align(s_root, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    lv_obj_set_style_pad_row(s_root, 6, 0);

    s_title = lv_label_create(s_root);
    lv_obj_set_style_text_font(s_title, &lv_font_montserrat_28, 0);
    lv_obj_set_style_text_color(s_title, UI_COLOUR_VALUE, 0);
    lv_label_set_text(s_title, "");

    for (int i = 0; i < CN_FIELDS_MAX; i++) {
        s_fields[i] = make_field(s_root);
    }

    lv_obj_set_hidden(s_root, true);
    return s_root;
}

void value_stack_render(const cn_state_t *state)
{
    lv_label_set_text(s_title, state->page_title);

    for (uint8_t i = 0; i < CN_FIELDS_MAX; i++) {
        stack_field_t *ui = &s_fields[i];
        if (i >= state->field_count) {
            lv_obj_set_hidden(ui->container, true);
            continue;
        }

        const cn_field_t *field = &state->fields[i];
        bool big = i == 0 || state->layout == CN_LAYOUT_DUAL;

        lv_label_set_text(ui->label, field->label);
        ui_value_set_font(&ui->value, big ? &lv_font_montserrat_48 : &lv_font_montserrat_28);
        ui_value_show(&ui->value, field, field->pending ? UI_COLOUR_PENDING : UI_COLOUR_VALUE);
        lv_obj_set_hidden(ui->container, false);
    }
}
