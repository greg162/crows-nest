/*
 * The screen, and which view is on it.
 *
 * Each layout from spec §5.5 is its own file under views/; this file owns what they share:
 * the black ground, the page dots under the view, the sim status near the bottom edge and
 * the notice banner near the top. A frame picks its view by `layout`. A layout this
 * firmware does not know falls back to the value stack, which can show anything.
 */

#include "crowsnest_ui.h"

#include "esp_log.h"
#include "ui_view.h"

static const char *TAG = "ui";

/* More pages than this get "2 / 9" in place of dots. */
#define UI_DOTS_MAX 8
#define UI_DOT_SIZE 10

static lv_obj_t *s_screen;
static lv_obj_t *s_pair;
static lv_obj_t *s_stack;
static lv_obj_t *s_waiting;
static lv_obj_t *s_dots;
static lv_obj_t *s_dot[UI_DOTS_MAX];
static lv_obj_t *s_page_text;
static lv_obj_t *s_status;
static lv_obj_t *s_notice;

/* Floating: placed against the screen's edge and left out of the column, so it showing or
 * hiding never moves the view. */
static lv_obj_t *edge_label(lv_align_t align, int32_t y, lv_color_t colour)
{
    lv_obj_t *label = lv_label_create(s_screen);
    lv_obj_set_floating(label, true);
    lv_obj_set_style_text_font(label, &lv_font_montserrat_14, 0);
    lv_obj_set_style_text_color(label, colour, 0);
    lv_obj_align(label, align, 0, y);
    lv_label_set_text(label, "");
    lv_obj_set_hidden(label, true);
    return label;
}

static void make_page_indicator(void)
{
    s_dots = ui_container(s_screen);
    lv_obj_set_flex_flow(s_dots, LV_FLEX_FLOW_ROW);
    lv_obj_set_flex_align(s_dots, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    lv_obj_set_style_pad_column(s_dots, 10, 0);

    for (int i = 0; i < UI_DOTS_MAX; i++) {
        s_dot[i] = ui_container(s_dots);
        lv_obj_set_size(s_dot[i], UI_DOT_SIZE, UI_DOT_SIZE);
        lv_obj_set_style_radius(s_dot[i], LV_RADIUS_CIRCLE, 0);
        lv_obj_set_style_bg_color(s_dot[i], UI_COLOUR_VALUE, 0);
        lv_obj_set_style_border_color(s_dot[i], UI_COLOUR_DIM, 0);
    }

    s_page_text = lv_label_create(s_dots);
    lv_obj_set_style_text_font(s_page_text, &lv_font_montserrat_14, 0);
    lv_obj_set_style_text_color(s_page_text, UI_COLOUR_LABEL, 0);
    lv_label_set_text(s_page_text, "");

    lv_obj_set_hidden(s_dots, true);
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
    lv_obj_set_style_pad_row(s_screen, 22, 0);
    lv_obj_set_scrollable(s_screen, false);

    s_pair = radio_pair_create(s_screen);
    s_stack = value_stack_create(s_screen);
    s_waiting = waiting_create(s_screen);
    make_page_indicator();

    /* On a 480 px circle the chord 60 px in from the edge is about 300 px wide: room for a
     * short line of small text. */
    s_status = edge_label(LV_ALIGN_BOTTOM_MID, -60, UI_COLOUR_PENDING);
    s_notice = edge_label(LV_ALIGN_TOP_MID, 60, UI_COLOUR_NOTICE);

    ESP_LOGI(TAG, "ui ready");
    return ESP_OK;
}

static void show_only(lv_obj_t *view)
{
    lv_obj_t *views[] = { s_pair, s_stack, s_waiting };
    for (size_t i = 0; i < sizeof views / sizeof views[0]; i++) {
        lv_obj_set_hidden(views[i], views[i] != view);
    }
}

static void render_page_indicator(int index, int count)
{
    if (count <= 1) {
        lv_obj_set_hidden(s_dots, true);
        return;
    }

    bool dots = count <= UI_DOTS_MAX;
    for (int i = 0; i < UI_DOTS_MAX; i++) {
        if (!dots || i >= count) {
            lv_obj_set_hidden(s_dot[i], true);
            continue;
        }

        /* The current page is a filled dot; the others are rings. */
        bool current = i == index;
        lv_obj_set_style_bg_opa(s_dot[i], current ? LV_OPA_COVER : LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(s_dot[i], current ? 0 : 2, 0);
        lv_obj_set_hidden(s_dot[i], false);
    }

    if (dots) {
        lv_obj_set_hidden(s_page_text, true);
    } else {
        lv_label_set_text_fmt(s_page_text, "%d / %d", index + 1, count);
        lv_obj_set_hidden(s_page_text, false);
    }

    lv_obj_set_hidden(s_dots, false);
}

/* Said only when something is wrong: with the sim connected the screen is just the radio. */
static const char *sim_problem(cn_sim_state_t sim)
{
    switch (sim) {
    case CN_SIM_CONNECTED:
        return NULL;
    case CN_SIM_CONNECTING:
        return "SIM CONNECTING";
    case CN_SIM_FAULTED:
        return "SIM FAULT";
    default:
        return "NO SIM";
    }
}

void crowsnest_ui_render(const cn_state_t *state)
{
    if (state == NULL || s_screen == NULL) {
        return;
    }

    if (state->layout == CN_LAYOUT_PAIR) {
        radio_pair_render(state);
        show_only(s_pair);
    } else {
        value_stack_render(state);
        show_only(s_stack);
    }

    render_page_indicator(state->page_index, state->page_count);

    const char *problem = sim_problem(state->sim);
    if (problem != NULL) {
        lv_label_set_text(s_status, problem);
        lv_obj_set_hidden(s_status, false);
    } else {
        lv_obj_set_hidden(s_status, true);
    }

    lv_obj_set_hidden(s_notice, true);
}

void crowsnest_ui_show_waiting(const char *hardware_id)
{
    if (s_screen == NULL) {
        return;
    }

    waiting_render(hardware_id);
    show_only(s_waiting);
    lv_obj_set_hidden(s_dots, true);
    lv_obj_set_hidden(s_status, true);
    lv_obj_set_hidden(s_notice, true);
}

void crowsnest_ui_show_notice(const char *kind)
{
    if (s_notice == NULL) {
        return;
    }

    lv_label_set_text(s_notice, kind != NULL ? kind : "");
    lv_obj_set_hidden(s_notice, false);
}
