/*
 * Crowsnest panel firmware.
 *
 * A thin client (spec §3.1): it renders pre-formatted text the host sends and reports
 * what the knob and the screen did. It holds no tuning state, knows no units, and has
 * never heard of a radio.
 *
 * Tasks (spec §9.3):
 *   lvgl_task     core 1   lv_timer_handler() every 5 ms; the only task that touches LVGL
 *   link_rx_task  core 0   USB CDC read, frame split, parse, apply
 *   link_tx_task  core 0   drains the outbound queue
 *   input_task    core 0   polls crowsnest_input, emits detent and button events
 */

#include <inttypes.h>
#include <stdio.h>
#include <string.h>

#include "bsp/esp-bsp.h"
#include "crowsnest_input.h"
#include "crowsnest_link.h"
#include "crowsnest_ui.h"
#include "driver/usb_serial_jtag.h"
#include "esp_log.h"
#include "esp_mac.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/task.h"

static const char *TAG = "crowsnest";

#define FIRMWARE_VERSION "0.1.0"
#define DEVICE_TYPE "crowpanel-2.1-rotary"

/* One outbound frame. Sized to the largest encoder output, which is the hello. */
#define TX_FRAME_MAX 320
#define TX_QUEUE_DEPTH 8

/* The USB CDC endpoint is ours alone: the console goes out UART0 (F2, see
 * sdkconfig.defaults). Nothing in this firmware may printf to stdout. */
#define LINK_RX_CHUNK 256

typedef struct {
    uint16_t len;
    char     bytes[TX_FRAME_MAX];
} tx_frame_t;

static QueueHandle_t s_tx_queue;
static cn_link_rx_t s_rx;
static char s_hardware_id[13];
static int32_t s_sequence;
static int32_t s_applied_revision = -1;
static bool s_host_seen;

/* ---- Transmit ------------------------------------------------------------------------ */

static void link_send(const char *bytes, int len)
{
    if (len <= 0 || len > TX_FRAME_MAX || s_tx_queue == NULL) {
        return;
    }

    tx_frame_t frame;
    frame.len = (uint16_t)len;
    memcpy(frame.bytes, bytes, (size_t)len);

    /* Never block the caller. A full queue means the host has stopped reading, and the
     * right thing to drop is the oldest input rather than to stall the input task. */
    if (xQueueSend(s_tx_queue, &frame, 0) != pdTRUE) {
        tx_frame_t discarded;
        (void)xQueueReceive(s_tx_queue, &discarded, 0);
        (void)xQueueSend(s_tx_queue, &frame, 0);
    }
}

static void link_tx_task(void *arg)
{
    (void)arg;

    tx_frame_t frame;
    for (;;) {
        if (xQueueReceive(s_tx_queue, &frame, portMAX_DELAY) == pdTRUE) {
            usb_serial_jtag_write_bytes(frame.bytes, frame.len, pdMS_TO_TICKS(100));
        }
    }
}

static void send_hello(void)
{
    const cn_hello_info_t info = {
        .device_type = DEVICE_TYPE,
        .firmware_version = FIRMWARE_VERSION,
        .hardware_id = s_hardware_id,
        .shape = "round",
        .width = BSP_LCD_H_RES,
        .height = BSP_LCD_V_RES,
        .has_encoder = true,
        .detents_per_click = 4,
        /* The CST8xx driver is not wired up yet, so this panel honestly reports no touch
         * and the host will not hand it a page whose only swap gesture is a tap. */
        .has_touch = false,
        .buttons = 1,
        .max_fields = CN_FIELDS_MAX,
    };

    char frame[TX_FRAME_MAX];
    link_send(frame, cn_link_encode_hello(frame, sizeof frame, &info));
}

/* ---- Receive -------------------------------------------------------------------------- */

static void apply_state(const cn_state_t *state)
{
    /* rev is monotonic; anything not newer than what is on screen is a replay or a
     * reordered frame and is discarded (spec §6.1). */
    if (state->revision <= s_applied_revision) {
        return;
    }
    s_applied_revision = state->revision;

    if (bsp_display_lock(200)) {
        crowsnest_ui_render(state);
        bsp_display_unlock();
    }
}

static void on_frame(const char *line, size_t len, void *user)
{
    (void)user;

    cn_msg_t message;
    if (!cn_link_parse(line, len, &message)) {
        /* Malformed, or a message type from a newer host. Ignored, never fatal. */
        return;
    }

    switch (message.type) {
    case CN_MSG_HELLO:
        s_host_seen = true;
        send_hello();
        break;

    case CN_MSG_HELLO_ACK:
        bsp_display_brightness_set(message.as.hello_ack.brightness);
        break;

    case CN_MSG_PING: {
        char frame[64];
        link_send(frame, cn_link_encode_pong(frame, sizeof frame, message.as.ping.timestamp));
        break;
    }

    case CN_MSG_STATE:
        apply_state(&message.as.state);
        break;

    case CN_MSG_NOTICE:
        if (bsp_display_lock(200)) {
            crowsnest_ui_show_notice(message.as.notice.kind);
            bsp_display_unlock();
        }
        break;

    default:
        break;
    }
}

static void link_rx_task(void *arg)
{
    (void)arg;

    cn_link_rx_init(&s_rx);

    char chunk[LINK_RX_CHUNK];
    for (;;) {
        int read = usb_serial_jtag_read_bytes(chunk, sizeof chunk, pdMS_TO_TICKS(100));
        if (read > 0) {
            cn_link_rx_feed(&s_rx, chunk, (size_t)read, on_frame, NULL);
        }
    }
}

/* ---- Input ---------------------------------------------------------------------------- */

#define INPUT_POLL_MS 20
#define LONG_PRESS_MS 600

static void input_task(void *arg)
{
    (void)arg;

    bool was_pressed = false;
    TickType_t pressed_at = 0;
    bool long_sent = false;

    for (;;) {
        int detents = crowsnest_encoder_read_detents();
        if (detents != 0) {
            char frame[96];
            link_send(frame, cn_link_encode_encoder(frame, sizeof frame, ++s_sequence, detents));
        }

        bool pressed = crowsnest_button_is_pressed();
        TickType_t now = xTaskGetTickCount();

        if (pressed && !was_pressed) {
            pressed_at = now;
            long_sent = false;
        } else if (pressed && !long_sent && (now - pressed_at) >= pdMS_TO_TICKS(LONG_PRESS_MS)) {
            /* Report the long press the moment it qualifies rather than on release, so
             * the knob feels like it responded when the user expected it to. */
            char frame[96];
            link_send(frame, cn_link_encode_press(frame, sizeof frame, ++s_sequence, true));
            long_sent = true;
        } else if (!pressed && was_pressed && !long_sent) {
            char frame[96];
            link_send(frame, cn_link_encode_press(frame, sizeof frame, ++s_sequence, false));
        }

        was_pressed = pressed;
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

void app_main(void)
{
    read_hardware_id();
    ESP_LOGI(TAG, "Crowsnest %s on %s, id %s", FIRMWARE_VERSION, DEVICE_TYPE, s_hardware_id);

    lv_display_t *display = bsp_display_start();
    if (display == NULL) {
        ESP_LOGE(TAG, "no display; the link will still come up so the host can say why");
    } else if (bsp_display_lock(1000)) {
        ESP_ERROR_CHECK(crowsnest_ui_init(display));
        crowsnest_ui_show_waiting(s_hardware_id);
        bsp_display_unlock();
        ESP_ERROR_CHECK(bsp_display_backlight_on());
    }

    ESP_ERROR_CHECK(crowsnest_input_init());

    s_tx_queue = xQueueCreate(TX_QUEUE_DEPTH, sizeof(tx_frame_t));
    ESP_ERROR_CHECK(s_tx_queue != NULL ? ESP_OK : ESP_ERR_NO_MEM);

    usb_serial_jtag_driver_config_t usb_config = {
        .tx_buffer_size = 2048,
        .rx_buffer_size = 2048,
    };
    ESP_ERROR_CHECK(usb_serial_jtag_driver_install(&usb_config));

    xTaskCreatePinnedToCore(link_tx_task, "link_tx", 3072, NULL, 5, NULL, 0);
    xTaskCreatePinnedToCore(link_rx_task, "link_rx", 4096, NULL, 5, NULL, 0);
    xTaskCreatePinnedToCore(input_task, "input", 3072, NULL, 4, NULL, 0);

    /* Announce ourselves without waiting to be asked: the host may already have been
     * running when this panel was plugged in, and it is listening either way. */
    send_hello();

    /* Keep saying hello until the host answers. A panel plugged into a machine where
     * Crowsnest starts later must not sit silent forever. */
    while (!s_host_seen) {
        vTaskDelay(pdMS_TO_TICKS(1000));
        send_hello();
    }

    ESP_LOGI(TAG, "host connected");
    vTaskDelete(NULL);
}
