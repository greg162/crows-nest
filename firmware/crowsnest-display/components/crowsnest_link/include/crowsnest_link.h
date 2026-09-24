/*
 * crowsnest_link — the wire protocol, as the panel sees it (spec §6.1).
 *
 * Board-independent and peripheral-free on purpose. Framing, JSON parsing, the hello
 * handshake and the outbound event encoder touch no hardware, so this whole component
 * builds and runs on the IDF `linux` target under Unity (spec §11.1). Keep it that way:
 * the fewer IDF headers this needs, the more portable its test harness stays.
 *
 * The central rule (spec §6.1): the host sends TEXT, not values. Fields arrive as
 * formatted strings with a character span to underline. This component has no concept of
 * frequencies, altitudes, headings or units, which is what lets NAV, COM 2 and the entire
 * autopilot arrive without a firmware change.
 */

#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define CN_PROTOCOL_VERSION 2

/* Sized from the reference panel's reported caps (spec §6.1: maxFields 3). A field longer
 * than CN_TEXT_MAX is truncated, never overflowed — the host is told the limit in `caps`
 * and is expected to respect it. */
#define CN_FIELDS_MAX 3
#define CN_TEXT_MAX   24
#define CN_LABEL_MAX  12
#define CN_ID_MAX     24
#define CN_TITLE_MAX  24

/* One NDJSON frame. Larger than any legitimate frame, small enough that a wedged sender
 * cannot exhaust the heap. */
#define CN_RX_BUFFER_MAX 1024

typedef enum {
    CN_LAYOUT_UNKNOWN = 0,
    CN_LAYOUT_PAIR,   /* ActiveStandbyPair */
    CN_LAYOUT_SINGLE, /* SingleValue */
    CN_LAYOUT_DUAL,   /* DualValue */
} cn_layout_t;

typedef enum {
    CN_ROLE_PRIMARY = 0,
    CN_ROLE_SECONDARY,
    CN_ROLE_TERTIARY,
} cn_role_t;

typedef enum {
    CN_SIM_DISCONNECTED = 0,
    CN_SIM_CONNECTING,
    CN_SIM_CONNECTED,
    CN_SIM_FAULTED,
} cn_sim_state_t;

typedef struct {
    cn_role_t role;
    char      label[CN_LABEL_MAX];
    char      text[CN_TEXT_MAX];
    /* [start, end) into `text`, in characters. Both -1 when the field has no cursor. */
    int16_t cursor_start;
    int16_t cursor_end;
    bool    pending;
} cn_field_t;

typedef struct {
    int32_t        revision;
    int32_t        ack;
    cn_sim_state_t sim;
    char           page_id[CN_ID_MAX];
    char           page_title[CN_TITLE_MAX];
    cn_layout_t    layout;
    int16_t        page_index;
    int16_t        page_count;
    uint8_t        field_count;
    cn_field_t     fields[CN_FIELDS_MAX];
} cn_state_t;

typedef enum {
    CN_MSG_UNKNOWN = 0,
    CN_MSG_HELLO,     /* host announcing itself; the panel replies with its own hello */
    CN_MSG_HELLO_ACK, /* carries the panel's configuration */
    CN_MSG_STATE,
    CN_MSG_NOTICE,
    CN_MSG_PING,
} cn_msg_type_t;

typedef struct {
    cn_msg_type_t type;
    int32_t       version; /* the frame's "v". Recorded, never enforced (spec §6.1). */
    union {
        cn_state_t state;
        struct {
            /* An opaque correlation token the host chose, echoed back unchanged. Not
             * seconds, and not ours to interpret. The host keys its in-flight pings on
             * Stopwatch.GetTimestamp(), which passes INT32_MAX a few minutes after boot
             * and grows with uptime, so narrowing this to 32 bits made every pong echo a
             * value the host had never sent — it matched no pong and blocked forever.
             * 64 bits, and never narrower. */
            int64_t timestamp;
        } ping;
        struct {
            char kind[CN_LABEL_MAX * 2];
        } notice;
        struct {
            int32_t brightness;
            char    theme[8];
        } hello_ack;
    } as;
} cn_msg_t;

/*
 * Parses one frame, newline already stripped.
 *
 * Returns false for malformed JSON and for message types this firmware does not know.
 * Neither is an error: an unknown type is ignored so the two ends version independently.
 * Member order does not matter — "t" may appear anywhere in the object.
 */
bool cn_link_parse(const char *json, size_t len, cn_msg_t *out);

/* ---- Receive framing -------------------------------------------------------------- */

typedef void (*cn_link_frame_fn)(const char *line, size_t len, void *user);

typedef struct {
    char     buffer[CN_RX_BUFFER_MAX];
    size_t   used;
    bool     discarding; /* inside an over-long frame; skipping to the next newline */
    uint32_t oversized;  /* frames dropped for length. Diagnostics only. */
} cn_link_rx_t;

void cn_link_rx_init(cn_link_rx_t *rx);

/*
 * Feeds bytes from the transport and invokes `fn` once per complete frame.
 *
 * Tolerates a frame split across any number of calls, several frames in one call, CRLF,
 * blank lines and arbitrary binary garbage. An over-long frame is abandoned and the
 * reader resynchronises at the next newline rather than growing without bound — noise on
 * the line costs one frame, never the link.
 */
void cn_link_rx_feed(cn_link_rx_t *rx, const char *data, size_t len, cn_link_frame_fn fn, void *user);

/* ---- Transmit encoding ------------------------------------------------------------ */

typedef struct {
    const char *device_type;     /* "crowpanel-2.1-rotary" */
    const char *firmware_version;
    const char *hardware_id;     /* eFuse MAC, lower case hex, no separators */
    const char *shape;           /* "round" | "rect" */
    int         width;
    int         height;
    bool        has_encoder;
    int         detents_per_click;
    bool        has_touch;
    int         buttons;
    int         max_fields;
} cn_hello_info_t;

/*
 * Every encoder writes one complete newline-terminated frame into `out` and returns its
 * length, or a negative value if it would not fit. None of them allocate.
 */
int cn_link_encode_hello(char *out, size_t cap, const cn_hello_info_t *info);
int cn_link_encode_pong(char *out, size_t cap, int64_t timestamp);
int cn_link_encode_encoder(char *out, size_t cap, int32_t seq, int detents);
int cn_link_encode_press(char *out, size_t cap, int32_t seq, bool long_press);
int cn_link_encode_tap(char *out, size_t cap, int32_t seq, int x, int y);
int cn_link_encode_log(char *out, size_t cap, const char *level, const char *message);

#ifdef __cplusplus
}
#endif
