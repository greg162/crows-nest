#include "link.h"

#include <stdio.h>
#include <string.h>

#include "bsp/esp-bsp.h"
#include "crowsnest_input.h"
#include "crowsnest_link.h"
#include "crowsnest_ui.h"
#include "driver/usb_serial_jtag.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/task.h"

static const char *TAG = "link";

#define TX_QUEUE_DEPTH 8
#define LINK_RX_CHUNK 256

/* The host pings every 2 s and gives up after three missed pongs. Past this with no frame
 * at all, it has gone (quit, crashed, cable pulled on its side), and the panel must say so
 * rather than sit on the last frame looking alive but dead (found 2026-09-27). */
#define HOST_SILENCE_MS 7000

static const link_config_t *s_config;
static QueueHandle_t s_tx_queue;
static cn_link_rx_t s_rx;
static int32_t s_applied_revision = -1;
static volatile bool s_host_seen;
static TickType_t s_last_frame_at;

/* Why the chip last reset is reported to the host once. The panic backtrace goes out UART0,
 * which nothing reads on a desk, so without this a crash looks like a mystery reboot
 * (found 2026-09-30). The full backtrace is in the core dump: idf.py coredump-info. */
static bool s_reset_reported;

/* ---- Transmit ------------------------------------------------------------------------ */

void link_send(tx_frame_t *frame, int len)
{
    if (len <= 0 || len > TX_FRAME_MAX || s_tx_queue == NULL) {
        return;
    }
    frame->len = (uint16_t)len;

    if (xQueueSend(s_tx_queue, frame, 0) != pdTRUE) {
        tx_frame_t discarded;
        (void)xQueueReceive(s_tx_queue, &discarded, 0);
        (void)xQueueSend(s_tx_queue, frame, 0);
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

static void send_log(const char *level, const char *message)
{
    tx_frame_t frame;
    link_send(&frame, cn_link_encode_log(frame.bytes, sizeof frame.bytes, level, message));
}

void link_send_hello(void)
{
    const cn_hello_info_t info = {
        .device_type = s_config->device_type,
        .firmware_version = s_config->firmware_version,
        .hardware_id = s_config->hardware_id,
        .shape = "round",
        .width = BSP_LCD_H_RES,
        .height = BSP_LCD_V_RES,
        .has_encoder = true,
        /* crowsnest_input already turns edges into clicks, so each detent sent is one click. */
        .detents_per_click = 1,
        /* Honest about a touch controller that did not answer, so the host never hands this
         * panel a page whose only swap gesture is a tap. */
        .has_touch = crowsnest_touch_available(),
        .buttons = 1,
        .max_fields = CN_FIELDS_MAX,
    };

    tx_frame_t frame;
    link_send(&frame, cn_link_encode_hello(frame.bytes, sizeof frame.bytes, &info));
}

bool link_host_seen(void)
{
    return s_host_seen;
}

/* ---- Receive -------------------------------------------------------------------------- */

/* Takes the LVGL lock for a screen update, waiting as long as a redraw takes (0 = forever).
 * Waiting is safe: the LVGL task never waits on the link. Returns false, without locking,
 * when the display did not start: LVGL's lock does not exist then, and taking it asserts. */
static bool ui_lock(void)
{
    if (!s_config->display_ready) {
        return false;
    }
    bsp_display_lock(0);
    return true;
}

static const char *crash_name(esp_reset_reason_t reason)
{
    switch (reason) {
    case ESP_RST_PANIC:
        return "a panic";
    case ESP_RST_INT_WDT:
        return "the interrupt watchdog";
    case ESP_RST_TASK_WDT:
        return "the task watchdog";
    case ESP_RST_WDT:
        return "a watchdog";
    case ESP_RST_BROWNOUT:
        return "a brownout";
    case ESP_RST_CPU_LOCKUP:
        return "a CPU lockup";
    default:
        return NULL; /* power-on, flashing, the reset button: nothing went wrong */
    }
}

/* Sent with the first state frame, not the hello: by then the host is listening for logs. */
static void report_reset_reason(void)
{
    if (s_reset_reported) {
        return;
    }
    s_reset_reported = true;

    const char *crash = crash_name(s_config->reset_reason);
    if (crash == NULL) {
        return;
    }

    char message[96];
    snprintf(message, sizeof message, "the panel restarted after %s; run idf.py coredump-info for the backtrace", crash);
    send_log("error", message);
}

static void apply_state(const cn_state_t *state)
{
    report_reset_reason();

    /* rev is monotonic; anything not newer than what is on screen is a replay or a
     * reordered frame and is discarded (spec §6.1). */
    if (state->revision <= s_applied_revision) {
        return;
    }

    /* Counted only once it is on screen. With a 200 ms lock timeout, a busy LVGL task cost
     * the frame for good, since the host only sends one when something changes (found in
     * review, 2026-09-30). */
    if (ui_lock()) {
        crowsnest_ui_render(state);
        bsp_display_unlock();
    }
    s_applied_revision = state->revision;
}

static void on_frame(const char *line, size_t len, void *user)
{
    (void)user;

    cn_msg_t message;
    if (!cn_link_parse(line, len, &message)) {
        /* Malformed, or a message type from a newer host. Ignored, never fatal. */
        return;
    }

    /* Any frame means a host is there, and re-arms the silence check. Only a hello used to,
     * so a host that stalled past HOST_SILENCE_MS and then recovered left the check off for
     * good, and its later death went unnoticed (found in review, 2026-09-30). */
    s_last_frame_at = xTaskGetTickCount();
    s_host_seen = true;

    switch (message.type) {
    case CN_MSG_HELLO:
        /* A new host session numbers its frames from 1 again. Without this reset the panel
         * would discard every frame until the new revisions overtook the old session's,
         * and look frozen after a host restart (found 2026-09-27). */
        s_applied_revision = -1;
        link_send_hello();

        /* The console is not on USB (see the README), so the host's log is the only place
         * the pilot can learn about a dead display or why taps do nothing. */
        if (!s_config->display_ready) {
            send_log("error", "the display did not start; see the UART0 console for why");
        }
        if (!crowsnest_touch_available()) {
            send_log("warn", "no touch controller answered; taps are off");
        }
        break;

    case CN_MSG_HELLO_ACK:
        /* A host that was already connected when this panel restarted answers its hello
         * with only an ack, and then sends the screen. */
        bsp_display_brightness_set(message.as.hello_ack.brightness);
        break;

    case CN_MSG_PING: {
        tx_frame_t frame;
        link_send(&frame, cn_link_encode_pong(frame.bytes, sizeof frame.bytes, message.as.ping.timestamp));
        break;
    }

    case CN_MSG_STATE:
        apply_state(&message.as.state);
        break;

    case CN_MSG_NOTICE:
        if (ui_lock()) {
            crowsnest_ui_show_notice(message.as.notice.kind);
            bsp_display_unlock();
        }
        break;

    default:
        break;
    }
}

/* Back to the screen a fresh panel shows. The next host announces itself with a hello,
 * which this panel answers as it always does. */
static void host_lost(void)
{
    ESP_LOGW(TAG, "no frame from the host for %d ms; waiting for it again", HOST_SILENCE_MS);
    s_host_seen = false;
    s_applied_revision = -1;
    cn_link_rx_init(&s_rx); /* drop any half-received frame */

    if (ui_lock()) {
        crowsnest_ui_show_waiting(s_config->hardware_id);
        bsp_display_unlock();
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

        if (s_host_seen && (xTaskGetTickCount() - s_last_frame_at) > pdMS_TO_TICKS(HOST_SILENCE_MS)) {
            host_lost();
        }
    }
}

/* ---- Start ---------------------------------------------------------------------------- */

esp_err_t link_start(const link_config_t *config)
{
    if (config == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    s_config = config;

    s_tx_queue = xQueueCreate(TX_QUEUE_DEPTH, sizeof(tx_frame_t));
    if (s_tx_queue == NULL) {
        return ESP_ERR_NO_MEM;
    }

    usb_serial_jtag_driver_config_t usb_config = {
        .tx_buffer_size = 2048,
        .rx_buffer_size = 2048,
    };
    esp_err_t err = usb_serial_jtag_driver_install(&usb_config);
    if (err != ESP_OK) {
        return err;
    }

    xTaskCreatePinnedToCore(link_tx_task, "link_tx", 3072, NULL, 5, NULL, 0);
    xTaskCreatePinnedToCore(link_rx_task, "link_rx", 4096, NULL, 5, NULL, 0);
    return ESP_OK;
}
