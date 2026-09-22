#include "bsp/esp-bsp.h"

#include "driver/gpio.h"
#include "driver/ledc.h"
#include "esp_check.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_rgb.h"
#include "esp_log.h"
#include "esp_lvgl_port.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "rom/ets_sys.h"

static const char *TAG = "bsp";

static i2c_master_bus_handle_t s_bus;
static i2c_master_dev_handle_t s_expander;
static SemaphoreHandle_t s_i2c_mutex;
static esp_lcd_panel_handle_t s_panel;
static lv_display_t *s_display;

/*
 * PCF8574 output shadow.
 *
 * The part is quasi-bidirectional: writing 1 releases a pin to a weak pull-up, which is
 * how it reads as an input, and writing 0 drives it hard low. There is no direction
 * register, so the input pins (touch INT, encoder button) must be held at 1 in every write
 * or polling them stops working. Start with everything released.
 */
static uint8_t s_expander_shadow = 0xFF;

/* ---- I2C ----------------------------------------------------------------------------- */

esp_err_t bsp_i2c_init(void)
{
    if (s_bus != NULL) {
        return ESP_OK;
    }

    s_i2c_mutex = xSemaphoreCreateMutex();
    ESP_RETURN_ON_FALSE(s_i2c_mutex != NULL, ESP_ERR_NO_MEM, TAG, "no memory for the I2C mutex");

    const i2c_master_bus_config_t bus_config = {
        .i2c_port = I2C_NUM_0,
        .sda_io_num = BSP_I2C_SDA,
        .scl_io_num = BSP_I2C_SCL,
        .clk_source = I2C_CLK_SRC_DEFAULT,
        .glitch_ignore_cnt = 7,
        .flags.enable_internal_pullup = true,
    };
    ESP_RETURN_ON_ERROR(i2c_new_master_bus(&bus_config, &s_bus), TAG, "i2c bus");

    const i2c_device_config_t expander_config = {
        .dev_addr_length = I2C_ADDR_BIT_LEN_7,
        .device_address = BSP_I2C_ADDR_EXPANDER,
        .scl_speed_hz = 400000,
    };
    ESP_RETURN_ON_ERROR(i2c_master_bus_add_device(s_bus, &expander_config, &s_expander), TAG, "expander");

    return ESP_OK;
}

i2c_master_bus_handle_t bsp_i2c_get_handle(void)
{
    return s_bus;
}

bool bsp_i2c_lock(uint32_t timeout_ms)
{
    if (s_i2c_mutex == NULL) {
        return false;
    }
    return xSemaphoreTake(s_i2c_mutex, pdMS_TO_TICKS(timeout_ms)) == pdTRUE;
}

void bsp_i2c_unlock(void)
{
    if (s_i2c_mutex != NULL) {
        xSemaphoreGive(s_i2c_mutex);
    }
}

esp_err_t bsp_expander_set(uint8_t mask, bool level)
{
    if (s_expander == NULL) {
        return ESP_ERR_INVALID_STATE;
    }
    if (!bsp_i2c_lock(200)) {
        return ESP_ERR_TIMEOUT;
    }

    uint8_t next = level ? (uint8_t)(s_expander_shadow | mask) : (uint8_t)(s_expander_shadow & ~mask);
    esp_err_t err = i2c_master_transmit(s_expander, &next, 1, 200);
    if (err == ESP_OK) {
        s_expander_shadow = next;
    }

    bsp_i2c_unlock();
    return err;
}

esp_err_t bsp_expander_read(uint8_t *value)
{
    if (s_expander == NULL || value == NULL) {
        return ESP_ERR_INVALID_STATE;
    }
    if (!bsp_i2c_lock(200)) {
        return ESP_ERR_TIMEOUT;
    }

    esp_err_t err = i2c_master_receive(s_expander, value, 1, 200);

    bsp_i2c_unlock();
    return err;
}

/* ---- ST7701 configuration bus -------------------------------------------------------- */

/*
 * The ST7701's register interface is 3-wire 9-bit SPI: one D/C bit ahead of each byte, no
 * MISO. It is bit-banged here rather than taken from esp_lcd_panel_io_additions on
 * purpose — it runs exactly once at boot, at any speed the panel likes, and hand-rolling
 * it keeps the BSP free of a managed component whose API has moved between IDF releases.
 */

#define ST7701_DC_COMMAND 0
#define ST7701_DC_DATA 1

static void st7701_write9(uint8_t dc, uint8_t value)
{
    gpio_set_level(BSP_LCD_SPI_CS, 0);

    gpio_set_level(BSP_LCD_SPI_SDA, dc);
    gpio_set_level(BSP_LCD_SPI_SCK, 0);
    ets_delay_us(1);
    gpio_set_level(BSP_LCD_SPI_SCK, 1);
    ets_delay_us(1);

    for (int bit = 7; bit >= 0; bit--) {
        gpio_set_level(BSP_LCD_SPI_SDA, (value >> bit) & 1);
        gpio_set_level(BSP_LCD_SPI_SCK, 0);
        ets_delay_us(1);
        gpio_set_level(BSP_LCD_SPI_SCK, 1);
        ets_delay_us(1);
    }

    gpio_set_level(BSP_LCD_SPI_CS, 1);
    ets_delay_us(1);
}

static void st7701_cmd(uint8_t cmd)
{
    st7701_write9(ST7701_DC_COMMAND, cmd);
}

static void st7701_data(const uint8_t *data, size_t len)
{
    for (size_t i = 0; i < len; i++) {
        st7701_write9(ST7701_DC_DATA, data[i]);
    }
}

#define ST7701_SEQ(cmd, ...)                                       \
    do {                                                           \
        static const uint8_t payload[] = { __VA_ARGS__ };          \
        st7701_cmd(cmd);                                           \
        st7701_data(payload, sizeof payload);                      \
    } while (0)

/*
 * Elecrow's init sequence for this panel, transcribed from Arduino_GFX's
 * st7701_type5_init_operations — the sequence their own demo ships and the one §9.5 names.
 *
 * Do not tidy this. An ST7701 init is a long list of undocumented vendor registers where
 * one wrong byte produces a blank or scrambled panel, and the only thing that makes this
 * one trustworthy is that it is a faithful copy of a sequence known to drive this exact
 * display. 0xFF selects a register bank; the banks are not independent, so the order of
 * the blocks matters as much as their contents.
 */
static void st7701_run_init_sequence(void)
{
    /* Bank 0x10: display line count, porches, gamma. */
    ST7701_SEQ(0xFF, 0x77, 0x01, 0x00, 0x00, 0x10);
    ST7701_SEQ(0xC0, 0x3B, 0x00);
    ST7701_SEQ(0xC1, 0x0B, 0x02); /* VBP */
    ST7701_SEQ(0xC2, 0x00, 0x02);
    ST7701_SEQ(0xCC, 0x10);
    ST7701_SEQ(0xCD, 0x08);

    ST7701_SEQ(0xB0, /* positive voltage gamma control */
               0x02, 0x13, 0x1B, 0x0D, 0x10, 0x05, 0x08, 0x07,
               0x07, 0x24, 0x04, 0x11, 0x0E, 0x2C, 0x33, 0x1D);
    ST7701_SEQ(0xB1, /* negative voltage gamma control */
               0x05, 0x13, 0x1B, 0x0D, 0x11, 0x05, 0x08, 0x07,
               0x07, 0x24, 0x04, 0x11, 0x0E, 0x2C, 0x33, 0x1D);

    /* Bank 0x11: power rails, VCOM, gate timing. */
    ST7701_SEQ(0xFF, 0x77, 0x01, 0x00, 0x00, 0x11);
    ST7701_SEQ(0xB0, 0x5D);
    ST7701_SEQ(0xB1, 0x43); /* VCOM amplitude */
    ST7701_SEQ(0xB2, 0x81); /* VGH 12 V */
    ST7701_SEQ(0xB3, 0x80);
    ST7701_SEQ(0xB5, 0x43); /* VGL -8.3 V */
    ST7701_SEQ(0xB7, 0x85);
    ST7701_SEQ(0xB8, 0x20);
    ST7701_SEQ(0xC1, 0x78);
    ST7701_SEQ(0xC2, 0x78);
    ST7701_SEQ(0xD0, 0x88);

    ST7701_SEQ(0xE0, 0x00, 0x00, 0x02);
    ST7701_SEQ(0xE1, 0x03, 0xA0, 0x00, 0x00, 0x04, 0xA0, 0x00, 0x00, 0x00, 0x20, 0x20);
    ST7701_SEQ(0xE2, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
    ST7701_SEQ(0xE3, 0x00, 0x00, 0x11, 0x00);
    ST7701_SEQ(0xE4, 0x22, 0x00);
    ST7701_SEQ(0xE5, 0x05, 0xEC, 0xA0, 0xA0, 0x07, 0xEE, 0xA0, 0xA0,
               0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
    ST7701_SEQ(0xE6, 0x00, 0x00, 0x11, 0x00);
    ST7701_SEQ(0xE7, 0x22, 0x00);
    ST7701_SEQ(0xE8, 0x06, 0xED, 0xA0, 0xA0, 0x08, 0xEF, 0xA0, 0xA0,
               0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
    ST7701_SEQ(0xEB, 0x00, 0x00, 0x40, 0x40, 0x00, 0x00, 0x00);
    ST7701_SEQ(0xED, 0xFF, 0xFF, 0xFF, 0xBA, 0x0A, 0xBF, 0x45, 0xFF,
               0xFF, 0x54, 0xFB, 0xA0, 0xAB, 0xFF, 0xFF, 0xFF);
    ST7701_SEQ(0xEF, 0x10, 0x0D, 0x04, 0x08, 0x3F, 0x1F);

    /* Bank 0x13, then back to the user bank. */
    ST7701_SEQ(0xFF, 0x77, 0x01, 0x00, 0x00, 0x13);
    ST7701_SEQ(0xEF, 0x08);
    ST7701_SEQ(0xFF, 0x77, 0x01, 0x00, 0x00, 0x00);

    /* MADCTL. Bit 3 selects BGR, which is how this panel is wired (spec Appendix B):
     * if red and blue come out swapped, this is the byte to change to 0x00. */
    ST7701_SEQ(0x36, 0x08);
    /* COLMOD. 0x60 is what Elecrow's sequence sets, and it is what works against the
     * 16-bit RGB565 data bus on this board. 0x50 and 0x70 are 16- and 24-bit. */
    ST7701_SEQ(0x3A, 0x60);

    st7701_cmd(0x11); /* sleep out */
    vTaskDelay(pdMS_TO_TICKS(120));
    st7701_cmd(0x29); /* display on */
    vTaskDelay(pdMS_TO_TICKS(50));
}

static esp_err_t st7701_init(void)
{
    const gpio_config_t spi_pins = {
        .pin_bit_mask = (1ULL << BSP_LCD_SPI_CS) | (1ULL << BSP_LCD_SPI_SCK) | (1ULL << BSP_LCD_SPI_SDA),
        .mode = GPIO_MODE_OUTPUT,
        .pull_up_en = GPIO_PULLUP_DISABLE,
        .pull_down_en = GPIO_PULLDOWN_DISABLE,
        .intr_type = GPIO_INTR_DISABLE,
    };
    ESP_RETURN_ON_ERROR(gpio_config(&spi_pins), TAG, "spi pins");

    gpio_set_level(BSP_LCD_SPI_CS, 1);
    gpio_set_level(BSP_LCD_SPI_SCK, 1);
    gpio_set_level(BSP_LCD_SPI_SDA, 1);

    /* Panel power and reset both hang off the expander, so the bus has to be up first. */
    ESP_RETURN_ON_ERROR(bsp_expander_set(BSP_EXP_LCD_POWER, true), TAG, "lcd power");
    vTaskDelay(pdMS_TO_TICKS(20));
    ESP_RETURN_ON_ERROR(bsp_expander_set(BSP_EXP_LCD_RESET, false), TAG, "lcd reset low");
    vTaskDelay(pdMS_TO_TICKS(20));
    ESP_RETURN_ON_ERROR(bsp_expander_set(BSP_EXP_LCD_RESET, true), TAG, "lcd reset high");
    vTaskDelay(pdMS_TO_TICKS(120));

    st7701_run_init_sequence();
    return ESP_OK;
}

/* ---- RGB panel ----------------------------------------------------------------------- */

static esp_err_t rgb_panel_init(void)
{
    const esp_lcd_rgb_panel_config_t config = {
        .clk_src = LCD_CLK_SRC_DEFAULT,
        .data_width = 16,
        /* IDF 6.x replaced bits_per_pixel with an explicit in/out pair. Both are RGB565
         * here: the framebuffer LVGL draws into, and the 16 data lines to the panel. */
        .in_color_format = LCD_COLOR_FMT_RGB565,
        .out_color_format = LCD_COLOR_FMT_RGB565,
        .num_fbs = 1,
        /* The RGB peripheral streams the framebuffer straight out of PSRAM. A bounce
         * buffer in internal RAM keeps the fetch off the critical path, which is what
         * stops the panel tearing whenever anything else touches PSRAM (spec §9.6). */
        .bounce_buffer_size_px = BSP_LCD_H_RES * 10,
        .de_gpio_num = GPIO_NUM_40,
        .pclk_gpio_num = GPIO_NUM_41,
        .vsync_gpio_num = GPIO_NUM_7,
        .hsync_gpio_num = GPIO_NUM_15,
        .disp_gpio_num = GPIO_NUM_NC,
        /* esp_lcd orders the data bus B0-B4, G0-G5, R0-R4 (spec Appendix B). */
        .data_gpio_nums = {
            GPIO_NUM_5, GPIO_NUM_45, GPIO_NUM_48, GPIO_NUM_47, GPIO_NUM_21,
            GPIO_NUM_14, GPIO_NUM_13, GPIO_NUM_12, GPIO_NUM_11, GPIO_NUM_10, GPIO_NUM_9,
            GPIO_NUM_46, GPIO_NUM_3, GPIO_NUM_8, GPIO_NUM_18, GPIO_NUM_17,
        },
        .timings = {
            /* 514 x 514 total against a 16 MHz pixel clock is a little over 60 Hz. */
            .pclk_hz = 16 * 1000 * 1000,
            .h_res = BSP_LCD_H_RES,
            .v_res = BSP_LCD_V_RES,
            .hsync_pulse_width = 4,
            .hsync_back_porch = 20,
            .hsync_front_porch = 10,
            .vsync_pulse_width = 4,
            .vsync_back_porch = 20,
            .vsync_front_porch = 10,
            .flags.pclk_active_neg = true,
        },
        .flags.fb_in_psram = true,
    };

    ESP_RETURN_ON_ERROR(esp_lcd_new_rgb_panel(&config, &s_panel), TAG, "rgb panel");
    ESP_RETURN_ON_ERROR(esp_lcd_panel_reset(s_panel), TAG, "panel reset");
    ESP_RETURN_ON_ERROR(esp_lcd_panel_init(s_panel), TAG, "panel init");
    return ESP_OK;
}

/* ---- Backlight ----------------------------------------------------------------------- */

#define BSP_BACKLIGHT_TIMER LEDC_TIMER_0
#define BSP_BACKLIGHT_CHANNEL LEDC_CHANNEL_0
#define BSP_BACKLIGHT_MAX_DUTY 1023 /* 10-bit */

static esp_err_t backlight_init(void)
{
    const ledc_timer_config_t timer = {
        .speed_mode = LEDC_LOW_SPEED_MODE,
        .duty_resolution = LEDC_TIMER_10_BIT,
        .timer_num = BSP_BACKLIGHT_TIMER,
        .freq_hz = 5000,
        .clk_cfg = LEDC_AUTO_CLK,
    };
    ESP_RETURN_ON_ERROR(ledc_timer_config(&timer), TAG, "backlight timer");

    const ledc_channel_config_t channel = {
        .gpio_num = BSP_LCD_BACKLIGHT,
        .speed_mode = LEDC_LOW_SPEED_MODE,
        .channel = BSP_BACKLIGHT_CHANNEL,
        .timer_sel = BSP_BACKLIGHT_TIMER,
        .duty = 0,
        .hpoint = 0,
    };
    return ledc_channel_config(&channel);
}

esp_err_t bsp_display_brightness_set(int percent)
{
    if (percent < 0) {
        percent = 0;
    } else if (percent > 100) {
        percent = 100;
    }

    uint32_t duty = (uint32_t)((BSP_BACKLIGHT_MAX_DUTY * percent) / 100);
    ESP_RETURN_ON_ERROR(ledc_set_duty(LEDC_LOW_SPEED_MODE, BSP_BACKLIGHT_CHANNEL, duty), TAG, "duty");
    return ledc_update_duty(LEDC_LOW_SPEED_MODE, BSP_BACKLIGHT_CHANNEL);
}

esp_err_t bsp_display_backlight_on(void)
{
    return bsp_display_brightness_set(100);
}

esp_err_t bsp_display_backlight_off(void)
{
    return bsp_display_brightness_set(0);
}

/* ---- Bring-up ------------------------------------------------------------------------ */

lv_display_t *bsp_display_start(void)
{
    if (s_display != NULL) {
        return s_display;
    }

    ESP_ERROR_CHECK(bsp_i2c_init());
    ESP_ERROR_CHECK(backlight_init());
    ESP_ERROR_CHECK(st7701_init());
    ESP_ERROR_CHECK(rgb_panel_init());

    const lvgl_port_cfg_t lvgl_cfg = {
        .task_priority = 4,
        .task_stack = 6144,
        /* Pinned to core 1, alone: lvgl_task is the only task allowed to touch LVGL
         * (spec §9.3), and the link and input tasks live on core 0. */
        .task_affinity = 1,
        .timer_period_ms = 5,
        .task_max_sleep_ms = 500,
    };
    ESP_ERROR_CHECK(lvgl_port_init(&lvgl_cfg));

    const lvgl_port_display_cfg_t display_cfg = {
        .panel_handle = s_panel,
        /* Partial refresh out of internal DMA-capable RAM. A full 480x480 RGB565 buffer
         * is 460,800 bytes against 512 KB of SRAM shared with every task stack, so the
         * draw buffers are 40 lines each (spec §9.6). */
        .buffer_size = BSP_LCD_H_RES * 40,
        .double_buffer = true,
        .hres = BSP_LCD_H_RES,
        .vres = BSP_LCD_V_RES,
        .monochrome = false,
        .rotation = {
            .swap_xy = false,
            .mirror_x = false,
            .mirror_y = false,
        },
        .flags = {
            .buff_dma = true,
        },
    };

    /* lvgl_port_add_disp() is for panels reached through an esp_lcd IO handle (SPI, I80)
     * and asserts that one is present. An RGB panel has no IO handle — it is driven
     * straight out of a framebuffer — so it needs the RGB entry point. */
    const lvgl_port_display_rgb_cfg_t rgb_cfg = {
        .flags = {
            /* Matches bounce_buffer_size_px on the panel config above. */
            .bb_mode = true,
            /* Tearing avoidance wants two or three full screen-sized buffers and costs
             * frame rate. Not while the panel is showing text that changes a few times a
             * second; revisit if a page ever animates. */
            .avoid_tearing = false,
        },
    };

    s_display = lvgl_port_add_disp_rgb(&display_cfg, &rgb_cfg);
    if (s_display == NULL) {
        ESP_LOGE(TAG, "lvgl_port_add_disp failed; the panel is initialised but nothing will draw");
        return NULL;
    }

    ESP_LOGI(TAG, "display up: %dx%d, framebuffer in PSRAM", BSP_LCD_H_RES, BSP_LCD_V_RES);
    return s_display;
}

bool bsp_display_lock(uint32_t timeout_ms)
{
    return lvgl_port_lock(timeout_ms);
}

void bsp_display_unlock(void)
{
    lvgl_port_unlock();
}
