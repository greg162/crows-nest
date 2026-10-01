/*
 * Crowsnest panel firmware.
 *
 * A thin client (spec §3.1): it renders pre-formatted text the host sends and reports
 * what the knob and the screen did. It holds no tuning state, knows no units, and has
 * never heard of a radio.
 *
 * Tasks (spec §9.3):
 *   lvgl_task     core 1   lv_timer_handler() every 5 ms, under the LVGL lock
 *   link_rx_task  core 0   USB CDC read, frame split, parse, apply          (link.c)
 *   link_tx_task  core 0   drains the outbound queue                        (link.c)
 *   input_task    core 0   polls crowsnest_input, emits detent, button and tap events
 *
 * Any task may call LVGL, but only while holding the LVGL lock (bsp_display_lock, which is
 * esp_lvgl_port's mutex). link_rx_task renders frames that way.
 */

#include <stdio.h>

#include "bsp/esp-bsp.h"
#include "crowsnest_input.h"
#include "crowsnest_link.h"
#include "crowsnest_ui.h"
#include "esp_log.h"
#include "esp_mac.h"
#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "gestures.h"
#include "link.h"

static const char *TAG = "crowsnest";

#define FIRMWARE_VERSION "0.3.4"
#define DEVICE_TYPE "crowpanel-2.1-rotary"

#define INPUT_POLL_MS 20

static char s_hardware_id[13];
static link_config_t s_link_config;

/* Only input_task numbers inputs, so this needs no lock. */
static int32_t s_sequence;

/* ---- Input ---------------------------------------------------------------------------- */

static void input_task(void *arg)
{
    (void)arg;

    button_tracker_t button = { 0 };
    touch_tracker_t touch = { 0 };
    tx_frame_t frame;

    for (;;) {
        uint32_t now_ms = (uint32_t)pdTICKS_TO_MS(xTaskGetTickCount());

        int detents = crowsnest_encoder_read_detents();
        if (detents != 0) {
            link_send(&frame, cn_link_encode_encoder(frame.bytes, sizeof frame.bytes, ++s_sequence, detents));
        }

        button_event_t press = button_step(&button, crowsnest_button_is_pressed(), now_ms);
        if (press != BUTTON_EVENT_NONE) {
            link_send(&frame, cn_link_encode_press(frame.bytes, sizeof frame.bytes, ++s_sequence, press == BUTTON_EVENT_LONG));
        }

        bool down = false;
        int x = 0;
        int y = 0;
        int tap_x = 0;
        int tap_y = 0;
        /* A sample that failed to read is skipped, not taken as a lifted finger. */
        if (crowsnest_touch_available() && crowsnest_touch_read(&down, &x, &y) == ESP_OK &&
            touch_step(&touch, down, x, y, now_ms, &tap_x, &tap_y)) {
            link_send(&frame, cn_link_encode_tap(frame.bytes, sizeof frame.bytes, ++s_sequence, tap_x, tap_y));
        }

        vTaskDelay(pdMS_TO_TICKS(INPUT_POLL_MS));
    }
}

/* ---- Bring-up --------------------------------------------------------------------------- */

static void read_hardware_id(void)
{
    /* The eFuse MAC is the only stable panel identity: it survives reflash, replug and
     * hub port renumbering, and Windows will happily renumber COM ports across a reboot
     * with several identical boards attached (spec §6.2). */
    uint8_t mac[6] = { 0 };
    esp_read_mac(mac, ESP_MAC_WIFI_STA);
    snprintf(s_hardware_id, sizeof s_hardware_id, "%02x%02x%02x%02x%02x%02x",
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
}

/* False if the display did not start; the link still comes up so the host can say why. */
static bool start_display(void)
{
    lv_display_t *display = bsp_display_start();
    if (display == NULL) {
        ESP_LOGE(TAG, "no display; the link will still come up so the host can say why");
        return false;
    }

    if (!bsp_display_lock(1000)) {
        ESP_LOGE(TAG, "the display started but its lock never came free");
        return false;
    }
    ESP_ERROR_CHECK(crowsnest_ui_init(display));
    crowsnest_ui_show_waiting(s_hardware_id);
    bsp_display_unlock();
    ESP_ERROR_CHECK(bsp_display_backlight_on());
    return true;
}

void app_main(void)
{
    read_hardware_id();
    esp_reset_reason_t reset_reason = esp_reset_reason();
    ESP_LOGI(TAG, "Crowsnest %s on %s, id %s, reset reason %d", FIRMWARE_VERSION, DEVICE_TYPE, s_hardware_id, (int)reset_reason);

    bool display_ready = start_display();

    ESP_ERROR_CHECK(crowsnest_input_init());

    s_link_config = (link_config_t){
        .device_type = DEVICE_TYPE,
        .firmware_version = FIRMWARE_VERSION,
        .hardware_id = s_hardware_id,
        .reset_reason = reset_reason,
        .display_ready = display_ready,
    };
    ESP_ERROR_CHECK(link_start(&s_link_config));

    xTaskCreatePinnedToCore(input_task, "input", 3072, NULL, 4, NULL, 0);

    /* Announce ourselves without waiting to be asked: the host may already have been
     * running when this panel was plugged in, and it is listening either way. */
    link_send_hello();

    /* Keep saying hello until the host answers. A panel plugged into a machine where
     * Crowsnest starts later must not sit silent forever. */
    while (!link_host_seen()) {
        vTaskDelay(pdMS_TO_TICKS(1000));
        link_send_hello();
    }

    ESP_LOGI(TAG, "host connected");
    vTaskDelete(NULL);
}
