/*
 * The link to the host, as this firmware runs it (spec §6.1): the USB-Serial/JTAG driver,
 * the outbound queue and its task, and the receive task that parses frames and applies
 * them to the screen. The protocol itself is crowsnest_link; this is the plumbing around it.
 *
 * The USB CDC endpoint is ours alone: the console goes out UART0 (F2, see
 * sdkconfig.defaults). Nothing in this firmware may printf to stdout.
 */

#pragma once

#include <stdbool.h>
#include <stdint.h>

#include "esp_err.h"
#include "esp_system.h"

#ifdef __cplusplus
extern "C" {
#endif

/* One outbound frame. Sized to the largest encoder output, which is the hello. */
#define TX_FRAME_MAX 320

typedef struct {
    uint16_t len;
    char     bytes[TX_FRAME_MAX];
} tx_frame_t;

typedef struct {
    const char        *device_type;
    const char        *firmware_version;
    const char        *hardware_id;
    esp_reset_reason_t reset_reason;
    bool               display_ready; /* false: no LVGL, so the link never touches the screen */
} link_config_t;

/* Installs the USB driver and starts link_tx and link_rx. The config must outlive the link. */
esp_err_t link_start(const link_config_t *config);

/*
 * Queues a frame an encoder has written into frame->bytes; `len` is the encoder's return
 * value, and a negative one (it did not fit) sends nothing. Never blocks: a full queue
 * means the host has stopped reading, and the oldest queued frame is dropped to make room.
 * Callers encode straight into a tx_frame_t, so a frame sits on their stack only once.
 */
void link_send(tx_frame_t *frame, int len);

void link_send_hello(void);

/* True once any frame has arrived from a host, until the host goes quiet. */
bool link_host_seen(void);

#ifdef __cplusplus
}
#endif
