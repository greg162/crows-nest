/*
 * crowsnest_link — NDJSON framing, parsing and encoding for the panel end of the link.
 *
 * The JSON reader here is hand-rolled rather than cJSON. The protocol is a flat object
 * with one nested object and one small array, the frames are bounded, and the component
 * has to build on the IDF `linux` target with nothing but libc so Unity can exercise it
 * on a host (spec §11.1). A dependency-free ~400 lines buys that outright.
 *
 * Parsing is order-independent: every recognised member is read into a scratch struct and
 * the message type is decided afterwards. The C# writer emits "t" first and the spec's
 * examples put "v" first, so neither end may assume an order.
 */

#include "crowsnest_link.h"

#include <stdio.h>
#include <string.h>

/* ---- A very small JSON reader ------------------------------------------------------ */

typedef struct {
    const char *p;
    const char *end;
} jp_t;

static void jp_ws(jp_t *j)
{
    while (j->p < j->end && (*j->p == ' ' || *j->p == '\t' || *j->p == '\n' || *j->p == '\r')) {
        j->p++;
    }
}

static bool jp_peek(jp_t *j, char c)
{
    jp_ws(j);
    return j->p < j->end && *j->p == c;
}

static bool jp_take(jp_t *j, char c)
{
    if (!jp_peek(j, c)) {
        return false;
    }
    j->p++;
    return true;
}

static void emit(char *out, size_t cap, size_t *len, char c)
{
    if (out != NULL && *len + 1 < cap) {
        out[*len] = c;
    }
    (*len)++;
}

/* Copies an unescaped string into `out` (which may be NULL to skip). Always NUL
 * terminates. Text longer than the destination is truncated rather than overflowed. */
static bool jp_string(jp_t *j, char *out, size_t cap)
{
    if (!jp_take(j, '"')) {
        return false;
    }

    size_t len = 0;
    while (j->p < j->end) {
        char c = *j->p++;

        if (c == '"') {
            if (out != NULL && cap > 0) {
                out[len < cap - 1 ? len : cap - 1] = '\0';
            }
            return true;
        }

        if (c != '\\') {
            emit(out, cap, &len, c);
            continue;
        }

        if (j->p >= j->end) {
            return false;
        }

        char esc = *j->p++;
        switch (esc) {
        case '"': emit(out, cap, &len, '"'); break;
        case '\\': emit(out, cap, &len, '\\'); break;
        case '/': emit(out, cap, &len, '/'); break;
        case 'b': emit(out, cap, &len, '\b'); break;
        case 'f': emit(out, cap, &len, '\f'); break;
        case 'n': emit(out, cap, &len, '\n'); break;
        case 'r': emit(out, cap, &len, '\r'); break;
        case 't': emit(out, cap, &len, '\t'); break;
        case 'u': {
            if (j->end - j->p < 4) {
                return false;
            }
            unsigned code = 0;
            for (int i = 0; i < 4; i++) {
                char h = *j->p++;
                unsigned digit;
                if (h >= '0' && h <= '9') {
                    digit = (unsigned)(h - '0');
                } else if (h >= 'a' && h <= 'f') {
                    digit = (unsigned)(h - 'a' + 10);
                } else if (h >= 'A' && h <= 'F') {
                    digit = (unsigned)(h - 'A' + 10);
                } else {
                    return false;
                }
                code = (code << 4) | digit;
            }

            if (code < 0x80) {
                emit(out, cap, &len, (char)code);
            } else if (code < 0x800) {
                emit(out, cap, &len, (char)(0xC0 | (code >> 6)));
                emit(out, cap, &len, (char)(0x80 | (code & 0x3F)));
            } else if (code >= 0xD800 && code <= 0xDFFF) {
                /* A surrogate half. Nothing in this protocol is outside the BMP, so
                 * rather than carry pair state, substitute U+FFFD and move on. */
                emit(out, cap, &len, (char)0xEF);
                emit(out, cap, &len, (char)0xBF);
                emit(out, cap, &len, (char)0xBD);
            } else {
                emit(out, cap, &len, (char)(0xE0 | (code >> 12)));
                emit(out, cap, &len, (char)(0x80 | ((code >> 6) & 0x3F)));
                emit(out, cap, &len, (char)(0x80 | (code & 0x3F)));
            }
            break;
        }
        default:
            return false;
        }
    }

    return false; /* unterminated */
}

static bool jp_number(jp_t *j, double *out)
{
    jp_ws(j);
    const char *start = j->p;

    if (j->p < j->end && (*j->p == '-' || *j->p == '+')) {
        j->p++;
    }

    bool any = false;
    while (j->p < j->end && *j->p >= '0' && *j->p <= '9') {
        j->p++;
        any = true;
    }

    if (j->p < j->end && *j->p == '.') {
        j->p++;
        while (j->p < j->end && *j->p >= '0' && *j->p <= '9') {
            j->p++;
            any = true;
        }
    }

    if (any && j->p < j->end && (*j->p == 'e' || *j->p == 'E')) {
        j->p++;
        if (j->p < j->end && (*j->p == '-' || *j->p == '+')) {
            j->p++;
        }
        while (j->p < j->end && *j->p >= '0' && *j->p <= '9') {
            j->p++;
        }
    }

    if (!any) {
        return false;
    }

    /* strtod needs a NUL-terminated buffer; the frame is not one. The longest legitimate
     * number here is a revision counter, so a small stack copy is ample. */
    char scratch[32];
    size_t n = (size_t)(j->p - start);
    if (n >= sizeof scratch) {
        return false;
    }
    memcpy(scratch, start, n);
    scratch[n] = '\0';

    double value = 0;
    if (sscanf(scratch, "%lf", &value) != 1) {
        return false;
    }

    if (out != NULL) {
        *out = value;
    }
    return true;
}

static bool jp_keyword(jp_t *j, const char *word, bool *out, bool value)
{
    size_t n = strlen(word);
    if ((size_t)(j->end - j->p) < n || memcmp(j->p, word, n) != 0) {
        return false;
    }
    j->p += n;
    if (out != NULL) {
        *out = value;
    }
    return true;
}

static bool jp_skip_value(jp_t *j, int depth);

/* Walks an object, handing each member name to the caller. */
typedef bool (*jp_member_fn)(jp_t *j, const char *key, void *user, int depth);

static bool jp_object(jp_t *j, jp_member_fn fn, void *user, int depth)
{
    if (depth > 8) {
        return false; /* a frame this nested is not ours */
    }
    if (!jp_take(j, '{')) {
        return false;
    }
    if (jp_take(j, '}')) {
        return true;
    }

    for (;;) {
        char key[24];
        if (!jp_string(j, key, sizeof key)) {
            return false;
        }
        if (!jp_take(j, ':')) {
            return false;
        }
        if (!fn(j, key, user, depth + 1)) {
            return false;
        }
        if (jp_take(j, ',')) {
            continue;
        }
        return jp_take(j, '}');
    }
}

static bool jp_skip_member(jp_t *j, const char *key, void *user, int depth)
{
    (void)key;
    (void)user;
    return jp_skip_value(j, depth);
}

static bool jp_skip_value(jp_t *j, int depth)
{
    jp_ws(j);
    if (j->p >= j->end) {
        return false;
    }

    switch (*j->p) {
    case '"':
        return jp_string(j, NULL, 0);
    case '{':
        return jp_object(j, jp_skip_member, NULL, depth);
    case '[': {
        if (depth > 8) {
            return false;
        }
        j->p++;
        if (jp_take(j, ']')) {
            return true;
        }
        for (;;) {
            if (!jp_skip_value(j, depth + 1)) {
                return false;
            }
            if (jp_take(j, ',')) {
                continue;
            }
            return jp_take(j, ']');
        }
    }
    case 't':
        return jp_keyword(j, "true", NULL, true);
    case 'f':
        return jp_keyword(j, "false", NULL, false);
    case 'n':
        return jp_keyword(j, "null", NULL, false);
    default:
        return jp_number(j, NULL);
    }
}

/* ---- Frame parsing ----------------------------------------------------------------- */

typedef struct {
    char    type[16];
    bool    have_type;
    double  version;
    double  revision;
    double  ack;
    char    sim[16];
    double  timestamp;
    char    notice_kind[CN_LABEL_MAX * 2];
    double  brightness;
    char    theme[8];
    char    page_id[CN_ID_MAX];
    char    page_title[CN_TITLE_MAX];
    char    page_layout[12];
    double  page_index;
    double  page_count;
    uint8_t field_count;
    cn_field_t fields[CN_FIELDS_MAX];
} scratch_t;

static cn_layout_t layout_from(const char *s)
{
    if (strcmp(s, "pair") == 0) {
        return CN_LAYOUT_PAIR;
    }
    if (strcmp(s, "single") == 0) {
        return CN_LAYOUT_SINGLE;
    }
    if (strcmp(s, "dual") == 0) {
        return CN_LAYOUT_DUAL;
    }
    return CN_LAYOUT_UNKNOWN; /* a layout this firmware has never heard of */
}

static cn_sim_state_t sim_from(const char *s)
{
    if (strcmp(s, "connected") == 0) {
        return CN_SIM_CONNECTED;
    }
    if (strcmp(s, "connecting") == 0) {
        return CN_SIM_CONNECTING;
    }
    if (strcmp(s, "faulted") == 0) {
        return CN_SIM_FAULTED;
    }
    return CN_SIM_DISCONNECTED;
}

static cn_role_t role_from(const char *s)
{
    if (strcmp(s, "secondary") == 0) {
        return CN_ROLE_SECONDARY;
    }
    if (strcmp(s, "tertiary") == 0) {
        return CN_ROLE_TERTIARY;
    }
    return CN_ROLE_PRIMARY;
}

static bool page_member(jp_t *j, const char *key, void *user, int depth)
{
    scratch_t *s = (scratch_t *)user;

    if (strcmp(key, "id") == 0) {
        return jp_string(j, s->page_id, sizeof s->page_id);
    }
    if (strcmp(key, "title") == 0) {
        return jp_string(j, s->page_title, sizeof s->page_title);
    }
    if (strcmp(key, "layout") == 0) {
        return jp_string(j, s->page_layout, sizeof s->page_layout);
    }
    if (strcmp(key, "index") == 0) {
        return jp_number(j, &s->page_index);
    }
    if (strcmp(key, "count") == 0) {
        return jp_number(j, &s->page_count);
    }
    return jp_skip_value(j, depth);
}

static bool field_member(jp_t *j, const char *key, void *user, int depth)
{
    cn_field_t *f = (cn_field_t *)user;

    if (strcmp(key, "role") == 0) {
        char role[12];
        if (!jp_string(j, role, sizeof role)) {
            return false;
        }
        f->role = role_from(role);
        return true;
    }
    if (strcmp(key, "label") == 0) {
        return jp_string(j, f->label, sizeof f->label);
    }
    if (strcmp(key, "text") == 0) {
        return jp_string(j, f->text, sizeof f->text);
    }
    if (strcmp(key, "pending") == 0) {
        bool value = false;
        if (jp_keyword(j, "true", &value, true) || jp_keyword(j, "false", &value, false)) {
            f->pending = value;
            return true;
        }
        return false;
    }
    if (strcmp(key, "cursor") == 0) {
        if (!jp_take(j, '[')) {
            return jp_skip_value(j, depth);
        }
        double start = 0;
        double end = 0;
        if (!jp_number(j, &start) || !jp_take(j, ',') || !jp_number(j, &end) || !jp_take(j, ']')) {
            return false;
        }
        f->cursor_start = (int16_t)start;
        f->cursor_end = (int16_t)end;
        return true;
    }
    return jp_skip_value(j, depth);
}

static bool cfg_member(jp_t *j, const char *key, void *user, int depth)
{
    scratch_t *s = (scratch_t *)user;

    if (strcmp(key, "brightness") == 0) {
        return jp_number(j, &s->brightness);
    }
    if (strcmp(key, "theme") == 0) {
        return jp_string(j, s->theme, sizeof s->theme);
    }
    return jp_skip_value(j, depth);
}

static bool top_member(jp_t *j, const char *key, void *user, int depth)
{
    scratch_t *s = (scratch_t *)user;

    if (strcmp(key, "t") == 0) {
        s->have_type = jp_string(j, s->type, sizeof s->type);
        return s->have_type;
    }
    if (strcmp(key, "v") == 0) {
        return jp_number(j, &s->version);
    }
    if (strcmp(key, "rev") == 0) {
        return jp_number(j, &s->revision);
    }
    if (strcmp(key, "ack") == 0) {
        return jp_number(j, &s->ack);
    }
    if (strcmp(key, "sim") == 0) {
        return jp_string(j, s->sim, sizeof s->sim);
    }
    if (strcmp(key, "ts") == 0) {
        return jp_number(j, &s->timestamp);
    }
    if (strcmp(key, "kind") == 0) {
        return jp_string(j, s->notice_kind, sizeof s->notice_kind);
    }
    if (strcmp(key, "page") == 0) {
        return jp_object(j, page_member, s, depth);
    }
    if (strcmp(key, "cfg") == 0) {
        return jp_object(j, cfg_member, s, depth);
    }
    if (strcmp(key, "fields") == 0) {
        if (!jp_take(j, '[')) {
            return jp_skip_value(j, depth);
        }
        if (jp_take(j, ']')) {
            return true;
        }
        for (;;) {
            if (s->field_count < CN_FIELDS_MAX) {
                cn_field_t *f = &s->fields[s->field_count];
                f->cursor_start = -1;
                f->cursor_end = -1;
                if (!jp_object(j, field_member, f, depth)) {
                    return false;
                }
                s->field_count++;
            } else {
                /* The host was told maxFields in the hello; anything past it is dropped
                 * rather than treated as an error. */
                if (!jp_skip_value(j, depth)) {
                    return false;
                }
            }

            if (jp_take(j, ',')) {
                continue;
            }
            return jp_take(j, ']');
        }
    }

    /* An unknown member. Skipped, never fatal (spec §6.1). */
    return jp_skip_value(j, depth);
}

bool cn_link_parse(const char *json, size_t len, cn_msg_t *out)
{
    if (json == NULL || out == NULL) {
        return false;
    }

    scratch_t s;
    memset(&s, 0, sizeof s);

    jp_t j = { .p = json, .end = json + len };
    if (!jp_object(&j, top_member, &s, 0) || !s.have_type) {
        return false;
    }

    memset(out, 0, sizeof *out);
    out->version = (int32_t)s.version;

    if (strcmp(s.type, "state") == 0) {
        out->type = CN_MSG_STATE;
        cn_state_t *st = &out->as.state;
        st->revision = (int32_t)s.revision;
        st->ack = (int32_t)s.ack;
        st->sim = sim_from(s.sim);
        st->layout = layout_from(s.page_layout);
        st->page_index = (int16_t)s.page_index;
        st->page_count = (int16_t)s.page_count;
        st->field_count = s.field_count;
        memcpy(st->page_id, s.page_id, sizeof st->page_id);
        memcpy(st->page_title, s.page_title, sizeof st->page_title);
        memcpy(st->fields, s.fields, sizeof st->fields);

        /* A cursor the host computed against a longer string than this firmware can hold
         * would underline the wrong characters. Dropping it is the honest failure. */
        for (uint8_t i = 0; i < st->field_count; i++) {
            cn_field_t *f = &st->fields[i];
            int16_t text_len = (int16_t)strlen(f->text);
            if (f->cursor_start < 0 || f->cursor_end > text_len || f->cursor_end <= f->cursor_start) {
                f->cursor_start = -1;
                f->cursor_end = -1;
            }
        }
        return true;
    }

    if (strcmp(s.type, "ping") == 0) {
        out->type = CN_MSG_PING;
        out->as.ping.timestamp = (int32_t)s.timestamp;
        return true;
    }

    if (strcmp(s.type, "hello") == 0) {
        out->type = CN_MSG_HELLO;
        return true;
    }

    if (strcmp(s.type, "hello_ack") == 0) {
        out->type = CN_MSG_HELLO_ACK;
        out->as.hello_ack.brightness = (int32_t)s.brightness;
        memcpy(out->as.hello_ack.theme, s.theme, sizeof out->as.hello_ack.theme);
        return true;
    }

    if (strcmp(s.type, "notice") == 0) {
        out->type = CN_MSG_NOTICE;
        memcpy(out->as.notice.kind, s.notice_kind, sizeof out->as.notice.kind);
        return true;
    }

    /* A message type from a newer host. Ignored, not an error. */
    out->type = CN_MSG_UNKNOWN;
    return false;
}

/* ---- Receive framing ---------------------------------------------------------------- */

void cn_link_rx_init(cn_link_rx_t *rx)
{
    if (rx != NULL) {
        memset(rx, 0, sizeof *rx);
    }
}

void cn_link_rx_feed(cn_link_rx_t *rx, const char *data, size_t len, cn_link_frame_fn fn, void *user)
{
    if (rx == NULL || data == NULL) {
        return;
    }

    for (size_t i = 0; i < len; i++) {
        char c = data[i];

        if (c == '\n') {
            if (rx->discarding) {
                /* The tail of a frame already given up on. Resynchronised now. */
                rx->discarding = false;
            } else if (rx->used > 0 && fn != NULL) {
                /* Tolerate CRLF from anything speaking to the link through a terminal. */
                size_t n = rx->used;
                if (rx->buffer[n - 1] == '\r') {
                    n--;
                }
                if (n > 0) {
                    rx->buffer[n] = '\0';
                    fn(rx->buffer, n, user);
                }
            }
            rx->used = 0;
            continue;
        }

        if (rx->discarding) {
            continue;
        }

        /* One byte short of the buffer, so a NUL terminator always fits. */
        if (rx->used + 1 >= sizeof rx->buffer) {
            rx->oversized++;
            rx->discarding = true;
            rx->used = 0;
            continue;
        }

        rx->buffer[rx->used++] = c;
    }
}

/* ---- Transmit encoding -------------------------------------------------------------- */

/* Escapes into a fixed buffer, truncating rather than overflowing. Only ever applied to
 * firmware log text, which is the one outbound string not under this component's control. */
static void json_escape(const char *in, char *out, size_t cap)
{
    size_t o = 0;
    for (const char *p = in; *p != '\0' && o + 7 < cap; p++) {
        unsigned char c = (unsigned char)*p;
        if (c == '"' || c == '\\') {
            out[o++] = '\\';
            out[o++] = (char)c;
        } else if (c < 0x20) {
            o += (size_t)snprintf(out + o, cap - o, "\\u%04x", c);
        } else {
            out[o++] = (char)c;
        }
    }
    out[o < cap ? o : cap - 1] = '\0';
}

static int finish(char *out, size_t cap, int written)
{
    /* snprintf reports what it WOULD have written; a truncated frame must never reach
     * the wire, because half a frame desynchronises the host's reader. */
    return (written < 0 || (size_t)written >= cap) ? -1 : written;
}

int cn_link_encode_hello(char *out, size_t cap, const cn_hello_info_t *info)
{
    if (out == NULL || info == NULL) {
        return -1;
    }

    return finish(out, cap, snprintf(
        out, cap,
        "{\"v\":%d,\"t\":\"hello\",\"dev\":\"%s\",\"fw\":\"%s\",\"id\":\"%s\","
        "\"caps\":{\"shape\":\"%s\",\"w\":%d,\"h\":%d,\"encoder\":%s,\"detentsPerClick\":%d,"
        "\"touch\":%s,\"buttons\":%d,\"maxFields\":%d,\"layouts\":[\"pair\",\"single\",\"dual\"]}}\n",
        CN_PROTOCOL_VERSION, info->device_type, info->firmware_version, info->hardware_id,
        info->shape, info->width, info->height, info->has_encoder ? "true" : "false",
        info->detents_per_click, info->has_touch ? "true" : "false", info->buttons,
        info->max_fields));
}

int cn_link_encode_pong(char *out, size_t cap, int32_t timestamp)
{
    return finish(out, cap, snprintf(
        out, cap, "{\"v\":%d,\"t\":\"pong\",\"ts\":%ld}\n",
        CN_PROTOCOL_VERSION, (long)timestamp));
}

int cn_link_encode_encoder(char *out, size_t cap, int32_t seq, int detents)
{
    return finish(out, cap, snprintf(
        out, cap, "{\"v\":%d,\"t\":\"input\",\"seq\":%ld,\"ev\":\"encoder\",\"d\":%d}\n",
        CN_PROTOCOL_VERSION, (long)seq, detents));
}

int cn_link_encode_press(char *out, size_t cap, int32_t seq, bool long_press)
{
    return finish(out, cap, snprintf(
        out, cap, "{\"v\":%d,\"t\":\"input\",\"seq\":%ld,\"ev\":\"press\",\"kind\":\"%s\"}\n",
        CN_PROTOCOL_VERSION, (long)seq, long_press ? "long" : "short"));
}

int cn_link_encode_tap(char *out, size_t cap, int32_t seq, int x, int y)
{
    return finish(out, cap, snprintf(
        out, cap, "{\"v\":%d,\"t\":\"input\",\"seq\":%ld,\"ev\":\"tap\",\"x\":%d,\"y\":%d}\n",
        CN_PROTOCOL_VERSION, (long)seq, x, y));
}

int cn_link_encode_log(char *out, size_t cap, const char *level, const char *message)
{
    char escaped[160];
    json_escape(message != NULL ? message : "", escaped, sizeof escaped);

    return finish(out, cap, snprintf(
        out, cap, "{\"v\":%d,\"t\":\"log\",\"lvl\":\"%s\",\"msg\":\"%s\"}\n",
        CN_PROTOCOL_VERSION, level != NULL ? level : "info", escaped));
}
