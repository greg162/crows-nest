#include <string.h>

#include "ui_view.h"

lv_obj_t *ui_container(lv_obj_t *parent)
{
    lv_obj_t *obj = lv_obj_create(parent);
    lv_obj_remove_style_all(obj);
    lv_obj_set_scrollable(obj, false);
    lv_obj_set_size(obj, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
    return obj;
}

static lv_obj_t *value_label(lv_obj_t *parent)
{
    lv_obj_t *label = lv_label_create(parent);
    lv_obj_set_style_pad_all(label, 0, 0);
    lv_label_set_text(label, "");
    return label;
}

ui_value_t ui_value_create(lv_obj_t *parent, const lv_font_t *font)
{
    ui_value_t value = { 0 };

    value.row = ui_container(parent);
    lv_obj_set_flex_flow(value.row, LV_FLEX_FLOW_ROW);
    lv_obj_set_flex_align(value.row, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_END, LV_FLEX_ALIGN_CENTER);

    value.before = value_label(value.row);
    value.cursor = value_label(value.row);
    value.after = value_label(value.row);

    /* The cursor is an underline under exactly the characters the host nominated. */
    lv_obj_set_style_border_color(value.cursor, UI_COLOUR_CURSOR, 0);
    lv_obj_set_style_border_width(value.cursor, 0, 0);
    lv_obj_set_style_border_side(value.cursor, LV_BORDER_SIDE_BOTTOM, 0);
    lv_obj_set_style_pad_bottom(value.cursor, 2, 0);

    ui_value_set_font(&value, font);
    return value;
}

void ui_value_set_font(ui_value_t *value, const lv_font_t *font)
{
    lv_obj_set_style_text_font(value->before, font, 0);
    lv_obj_set_style_text_font(value->cursor, font, 0);
    lv_obj_set_style_text_font(value->after, font, 0);
}

void ui_value_set_letter_space(ui_value_t *value, int32_t space)
{
    lv_obj_set_style_text_letter_space(value->before, space, 0);
    lv_obj_set_style_text_letter_space(value->cursor, space, 0);
    lv_obj_set_style_text_letter_space(value->after, space, 0);

    /* A label spaces the gaps inside it, not the one to the next label, so the row adds
     * that, or the cursor's digit would sit closer to its neighbours than they do to theirs. */
    lv_obj_set_style_pad_column(value->row, space, 0);
}

/* Sets a label's text and hides it when empty, so the row's column gap is not spent on it. */
static void set_part(lv_obj_t *label, const char *text)
{
    lv_label_set_text(label, text);
    lv_obj_set_hidden(label, text[0] == '\0');
}

void ui_value_show(ui_value_t *value, const cn_field_t *field, lv_color_t colour)
{
    lv_obj_set_style_text_color(value->before, colour, 0);
    lv_obj_set_style_text_color(value->cursor, colour, 0);
    lv_obj_set_style_text_color(value->after, colour, 0);

    int len = (int)strlen(field->text);
    int start = field->cursor_start;
    int end = field->cursor_end;

    if (start < 0 || end <= start || end > len) {
        /* No cursor: the whole value goes in one label and nothing is underlined. */
        set_part(value->before, field->text);
        set_part(value->cursor, "");
        set_part(value->after, "");
        lv_obj_set_style_border_width(value->cursor, 0, 0);
        return;
    }

    char buffer[CN_TEXT_MAX];

    memcpy(buffer, field->text, (size_t)start);
    buffer[start] = '\0';
    set_part(value->before, buffer);

    memcpy(buffer, field->text + start, (size_t)(end - start));
    buffer[end - start] = '\0';
    set_part(value->cursor, buffer);

    set_part(value->after, field->text + end);
    lv_obj_set_style_border_width(value->cursor, 4, 0);
}
