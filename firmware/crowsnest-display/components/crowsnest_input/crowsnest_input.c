#include "crowsnest_input.h"

#include "bsp/esp-bsp.h"
#include "driver/pulse_cnt.h"
#include "esp_check.h"
#include "esp_log.h"

static const char *TAG = "input";

/* The counter is read and zeroed on every poll, so the limits only need to exceed one
 * poll interval's worth of spinning. A fast flick is a few dozen counts at most. */
#define ENCODER_LIMIT_HIGH 1000
#define ENCODER_LIMIT_LOW (-1000)

/*
 * Quadrature on the ESP32-S3's PCNT, with the hardware glitch filter doing the debouncing
 * (spec §9.2). This knob is a half-step encoder: two edges per mechanical detent, measured
 * 2026-09-27 (at 4, the value changed on every other click). The conversion happens here,
 * so the host receives whole detents.
 */
#define ENCODER_EDGES_PER_DETENT 2

/*
 * +1 or -1. With the decode below, this board's wiring counts anticlockwise as positive;
 * the host expects clockwise to increase the value, as on a real radio (found 2026-09-27).
 */
#define ENCODER_CLOCKWISE_SIGN (-1)

static pcnt_unit_handle_t s_unit;
static int s_residual; /* edges left over from the last detent conversion */
static bool s_has_touch;

esp_err_t crowsnest_input_init(void)
{
    const pcnt_unit_config_t unit_config = {
        .high_limit = ENCODER_LIMIT_HIGH,
        .low_limit = ENCODER_LIMIT_LOW,
    };
    ESP_RETURN_ON_ERROR(pcnt_new_unit(&unit_config, &s_unit), TAG, "pcnt unit");

    /* 1 us at 80 MHz APB. Anything shorter than this on either line is contact bounce,
     * not a turn — without the filter a single detent reads as several. */
    const pcnt_glitch_filter_config_t filter = { .max_glitch_ns = 1000 };
    ESP_RETURN_ON_ERROR(pcnt_unit_set_glitch_filter(s_unit, &filter), TAG, "glitch filter");

    pcnt_chan_config_t channel_a_config = {
        .edge_gpio_num = BSP_ENCODER_A,
        .level_gpio_num = BSP_ENCODER_B,
    };
    pcnt_channel_handle_t channel_a = NULL;
    ESP_RETURN_ON_ERROR(pcnt_new_channel(s_unit, &channel_a_config, &channel_a), TAG, "channel a");

    pcnt_chan_config_t channel_b_config = {
        .edge_gpio_num = BSP_ENCODER_B,
        .level_gpio_num = BSP_ENCODER_A,
    };
    pcnt_channel_handle_t channel_b = NULL;
    ESP_RETURN_ON_ERROR(pcnt_new_channel(s_unit, &channel_b_config, &channel_b), TAG, "channel b");

    /* Both edges on both channels, each channel's direction taken from the other's level.
     * This is the standard x4 quadrature decode. */
    ESP_RETURN_ON_ERROR(
        pcnt_channel_set_edge_action(channel_a, PCNT_CHANNEL_EDGE_ACTION_DECREASE, PCNT_CHANNEL_EDGE_ACTION_INCREASE),
        TAG, "channel a edges");
    ESP_RETURN_ON_ERROR(
        pcnt_channel_set_level_action(channel_a, PCNT_CHANNEL_LEVEL_ACTION_KEEP, PCNT_CHANNEL_LEVEL_ACTION_INVERSE),
        TAG, "channel a levels");
    ESP_RETURN_ON_ERROR(
        pcnt_channel_set_edge_action(channel_b, PCNT_CHANNEL_EDGE_ACTION_INCREASE, PCNT_CHANNEL_EDGE_ACTION_DECREASE),
        TAG, "channel b edges");
    ESP_RETURN_ON_ERROR(
        pcnt_channel_set_level_action(channel_b, PCNT_CHANNEL_LEVEL_ACTION_KEEP, PCNT_CHANNEL_LEVEL_ACTION_INVERSE),
        TAG, "channel b levels");

    ESP_RETURN_ON_ERROR(pcnt_unit_enable(s_unit), TAG, "pcnt enable");
    ESP_RETURN_ON_ERROR(pcnt_unit_clear_count(s_unit), TAG, "pcnt clear");
    ESP_RETURN_ON_ERROR(pcnt_unit_start(s_unit), TAG, "pcnt start");

    ESP_LOGI(TAG, "encoder on GPIO %d/%d, button on expander P5", BSP_ENCODER_A, BSP_ENCODER_B);

    /* Not fatal: without touch the panel still tunes, and says so in its hello. */
    s_has_touch = bsp_touch_init() == ESP_OK;
    return ESP_OK;
}

int crowsnest_encoder_read_detents(void)
{
    if (s_unit == NULL) {
        return 0;
    }

    int count = 0;
    if (pcnt_unit_get_count(s_unit, &count) != ESP_OK) {
        return 0;
    }
    pcnt_unit_clear_count(s_unit);

    /* Carry the remainder rather than truncating it, or a slow turn never reaches a
     * whole detent and the knob feels dead. */
    int edges = (ENCODER_CLOCKWISE_SIGN * count) + s_residual;
    int detents = edges / ENCODER_EDGES_PER_DETENT;
    s_residual = edges - (detents * ENCODER_EDGES_PER_DETENT);
    return detents;
}

bool crowsnest_button_is_pressed(void)
{
    /* The knob button is expander P5 with a pull-up, so pressed reads low. There is no
     * interrupt line for it, which is why §9.3 gives input_task a 20 ms poll. */
    uint8_t value = 0xFF;
    if (bsp_expander_read(&value) != ESP_OK) {
        return false;
    }
    return (value & BSP_EXP_ENCODER_BUTTON) == 0;
}

bool crowsnest_touch_available(void)
{
    return s_has_touch;
}

esp_err_t crowsnest_touch_read(bool *down, int *x, int *y)
{
    if (!s_has_touch) {
        return ESP_ERR_NOT_SUPPORTED;
    }

    bsp_touch_point_t point;
    esp_err_t err = bsp_touch_read(&point);
    if (err != ESP_OK) {
        return err;
    }

    /* The panel is mounted unrotated (bsp_display_start), so the controller's axes are the
     * display's. Clamped so a stray reading never reaches the host as an off-screen point. */
    *down = point.pressed;
    *x = point.x < BSP_LCD_H_RES ? point.x : BSP_LCD_H_RES - 1;
    *y = point.y < BSP_LCD_V_RES ? point.y : BSP_LCD_V_RES - 1;
    return ESP_OK;
}
