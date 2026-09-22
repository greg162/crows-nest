#include "crowsnest_ui.h"

#include <string.h>

#include "esp_log.h"

static const char *TAG = "ui";

/* The panel is a 480 px circle. Anything wider than this chord near the top or bottom of
 * the screen runs into the bezel, so all content lives inside a centred column. */
#define UI_CONTENT_WIDTH 360

#define UI_COLOUR_BACKGROUND lv_color_hex(0x000000)
#define UI_COLOUR_VALUE lv_color_hex(0xFFFFFF)
#define UI_COLOUR_LABEL lv_color_hex(0x8A8A8A)
#define UI_COLOUR_PENDING lv_color_hex(0xFFB000) /* amber: written, not yet confirmed */
#define UI_COLOUR_CURSOR lv_color_hex(0x35B7FF)
#define UI_COLOUR_NOTICE lv_color_hex(0xFF5545)

/*
 * One field. The value is three labels in a row rather than one, so the cursor span can
 * carry its own underline style without measuring glyph widths — the text before the
 * cursor, the text under it, and the text after. A layout change to the font then cannot
 * put the underline under the wrong digits.
 */
typedef struct {
    lv_obj_t *container;
    lv_obj_t *label;
    lv_obj_t *value_row;
    lv_obj_t *before;
    lv_obj_t *cursor;
    lv_obj_t *after;
} ui_field_t;

static lv_obj_t *s_screen;
static lv_obj_t *s_title;
static lv_obj_t *s_page_indicator;
static lv_obj_t *s_status;
static lv_obj_t *s_notice;
static ui_field_t s_fields[CN_FIELDS_MAX];

static void style_value_label(lv_obj_t *label, const lv_font_t *font)
{
    lv_obj_set_style_text_font(label, font, 0);
    lv_obj_set_style_text_color(label, UI_COLOUR_VALUE, 0);
    lv_obj_set_style_pad_all(label, 0, 0);
}

static ui_field_t make_field(lv_obj_t *parent)
{
    ui_field_t field = { 0 };

    field.container = lv_obj_create(parent);
    lv_obj_remove_style_all(field.container);
    lv_obj_set_width(field.container, UI_CONTENT_WIDTH);
    lv_obj_set_height(field.container, LV_SIZE_CONTENT);
    lv_obj_set_flex_flow(field.container, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_flex_align(field.container, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    lv_obj_set_style_pad_row(field.container, 2, 0);

    field.label = lv_label_create(field.container);
    lv_obj_set_style_text_font(field.label, &lv_font_montserrat_14, 0);
    lv_obj_set_style_text_color(field.label, UI_COLOUR_LABEL, 0);
    lv_label_set_text(field.label, "");

    field.value_row = lv_obj_create(field.container);
    lv_obj_remove_style_all(field.value_row);
    lv_obj_set_size(field.value_row, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
    lv_obj_set_flex_flow(field.value_row, LV_FLEX_FLOW_ROW);
    lv_obj_set_flex_align(field.value_row, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_END, LV_FLEX_ALIGN_CENTER);
    lv_obj_set_style_pad_column(field.value_row, 0, 0);

    field.before = lv_label_create(field.value_row);
    field.cursor = lv_label_create(field.value_row);
    field.after = lv_label_create(field.value_row);

    style_value_label(field.before, &lv_font_montserrat_48);
    style_value_label(field.cursor, &lv_font_montserrat_48);
    style_value_label(field.after, &lv_font_montserrat_48);

    /* The cursor is an underline under exactly the characters the host nominated. */
    lv_obj_set_style_border_color(field.cursor, UI_COLOUR_CURSOR, 0);
    lv_obj_set_style_border_width(field.cursor, 0, 0);
    lv_obj_set_style_border_side(field.cursor, LV_BORDER_SIDE_BOTTOM, 0);
    lv_obj_set_style_pad_bottom(field.cursor, 2, 0);

    lv_label_set_text(field.before, "");
    lv_label_set_text(field.cursor, "");
    lv_label_set_text(field.after, "");

    return field;
}

esp_err_t crowsnest_ui_init(lv_display_t *display)
{
    if (display == NULL) {
        return ESP_ERR_INVALID_ARG;
    }

    s_screen = lv_display_get_screen_active(display);
    lv_obj_set_style_bg_color(s_screen, UI_COLOUR_BACKGROUND, 0);
    lv_obj_set_style_bg_opa(s_screen, LV_OPA_COVER, 0);
    lv_obj_set_flex_flow(s_screen, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_flex_align(s_screen, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    lv_obj_set_style_pad_row(s_screen, 6, 0);
    lv_obj_remove_flag(s_screen, LV_OBJ_FLAG_SCROLLABLE);

    s_title = lv_label_create(s_screen);
    lv_obj_set_style_text_font(s_title, &lv_font_montserrat_28, 0);
    lv_obj_set_style_text_color(s_title, UI_COLOUR_VALUE, 0);
    lv_label_set_text(s_title, "CROWSNEST");

    s_page_indicator = lv_label_create(s_screen);
    lv_obj_set_style_text_font(s_page_indicator, &lv_font_montserrat_14, 0);
    lv_obj_set_style_text_color(s_page_indicator, UI_COLOUR_LABEL, 0);
    lv_label_set_text(s_page_indicator, "");

    for (int i = 0; i < CN_FIELDS_MAX; i++) {
        s_fields[i] = make_field(s_screen);
        lv_obj_add_flag(s_fields[i].container, LV_OBJ_FLAG_HIDDEN);
    }

    s_status = lv_label_create(s_screen);
    lv_obj_set_style_text_font(s_status, &lv_font_montserrat_14, 0);
    lv_obj_set_style_text_color(s_status, UI_COLOUR_LABEL, 0);
    lv_label_set_text(s_status, "");

    s_notice = lv_label_create(s_screen);
    lv_obj_set_style_text_font(s_notice, &lv_font_montserrat_14, 0);
    lv_obj_set_style_text_color(s_notice, UI_COLOUR_NOTICE, 0);
    lv_label_set_text(s_notice, "");
    lv_obj_add_flag(s_notice, LV_OBJ_FLAG_HIDDEN);

    ESP_LOGI(TAG, "ui ready");
    return ESP_OK;
}

static const char *sim_text(cn_sim_state_t sim)
{
    switch (sim) {
    case CN_SIM_CONNECTED:
        return "SIM CONNECTED";
    case CN_SIM_CONNECTING:
        return "SIM CONNECTING";
    case CN_SIM_FAULTED:
        return "SIM FAULT";
    default:
        return "NO SIM";
    }
}

/* Primary gets the big font; everything else is secondary furniture. A SingleValue page
 * therefore looks the same as the top half of a pair, which is the point — one renderer,
 * every layout (spec §5.5). */
static const lv_font_t *font_for(const cn_state_t *state, uint8_t index)
{
    if (index == 0) {
        return &lv_font_montserrat_48;
    }
    return state->layout == CN_LAYOUT_DUAL ? &lv_font_montserrat_48 : &lv_font_montserrat_28;
}

static void render_field(ui_field_t *ui, const cn_field_t *field, const lv_font_t *font)
{
    lv_label_set_text(ui->label, field->label);

    lv_obj_set_style_text_font(ui->before, font, 0);
    lv_obj_set_style_text_font(ui->cursor, font, 0);
    lv_obj_set_style_text_font(ui->after, font, 0);

    lv_color_t colour = field->pending ? UI_COLOUR_PENDING : UI_COLOUR_VALUE;
    lv_obj_set_style_text_color(ui->before, colour, 0);
    lv_obj_set_style_text_color(ui->cursor, colour, 0);
    lv_obj_set_style_text_color(ui->after, colour, 0);

    int len = (int)strlen(field->text);
    int start = field->cursor_start;
    int end = field->cursor_end;

    if (start < 0 || end <= start || end > len) {
        /* No cursor: the whole value goes in one label and nothing is underlined. */
        lv_label_set_text(ui->before, field->text);
        lv_label_set_text(ui->cursor, "");
        lv_label_set_text(ui->after, "");
        lv_obj_set_style_border_width(ui->cursor, 0, 0);
        return;
    }

    char buffer[CN_TEXT_MAX];

    memcpy(buffer, field->text, (size_t)start);
    buffer[start] = '\0';
    lv_label_set_text(ui->before, buffer);

    memcpy(buffer, field->text + start, (size_t)(end - start));
    buffer[end - start] = '\0';
    lv_label_set_text(ui->cursor, buffer);

    lv_label_set_text(ui->after, field->text + end);
    lv_obj_set_style_border_width(ui->cursor, 4, 0);
}

void crowsnest_ui_render(const cn_state_t *state)
{
    if (state == NULL || s_screen == NULL) {
        return;
    }

    lv_label_set_text(s_title, state->page_title);

    if (state->page_count > 1) {
        lv_label_set_text_fmt(s_page_indicator, "%d / %d", state->page_index + 1, state->page_count);
    } else {
        lv_label_set_text(s_page_indicator, "");
    }

    for (uint8_t i = 0; i < CN_FIELDS_MAX; i++) {
        if (i < state->field_count) {
            render_field(&s_fields[i], &state->fields[i], font_for(state, i));
            lv_obj_remove_flag(s_fields[i].container, LV_OBJ_FLAG_HIDDEN);
        } else {
            lv_obj_add_flag(s_fields[i].container, LV_OBJ_FLAG_HIDDEN);
        }
    }

    lv_label_set_text(s_status, sim_text(state->sim));
    lv_obj_add_flag(s_notice, LV_OBJ_FLAG_HIDDEN);
}

void crowsnest_ui_show_waiting(const char *hardware_id)
{
    if (s_screen == NULL) {
        return;
    }

    lv_label_set_text(s_title, "CROWSNEST");
    lv_label_set_text(s_page_indicator, "");

    for (int i = 0; i < CN_FIELDS_MAX; i++) {
        lv_obj_add_flag(s_fields[i].container, LV_OBJ_FLAG_HIDDEN);
    }

    cn_field_t waiting = {
        .role = CN_ROLE_PRIMARY,
        .cursor_start = -1,
        .cursor_end = -1,
    };
    /* The last six characters of the hardware id: enough to match a panel against a
     * settings entry, short enough to read across a cockpit (spec §6.2). */
    size_t id_len = hardware_id != NULL ? strlen(hardware_id) : 0;
    const char *tail = id_len > 6 ? hardware_id + id_len - 6 : (hardware_id != NULL ? hardware_id : "??????");
    snprintf(waiting.label, sizeof waiting.label, "PANEL");
    snprintf(waiting.text, sizeof waiting.text, "%s", tail);

    render_field(&s_fields[0], &waiting, &lv_font_montserrat_28);
    lv_obj_remove_flag(s_fields[0].container, LV_OBJ_FLAG_HIDDEN);

    lv_label_set_text(s_status, "waiting for Crowsnest");
    lv_obj_add_flag(s_notice, LV_OBJ_FLAG_HIDDEN);
}

void crowsnest_ui_show_notice(const char *kind)
{
    if (s_notice == NULL) {
        return;
    }

    lv_label_set_text(s_notice, kind != NULL ? kind : "");
    lv_obj_remove_flag(s_notice, LV_OBJ_FLAG_HIDDEN);
}
