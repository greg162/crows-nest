/*
 * bsp_crowpanel_21_rotary — board support for the Elecrow CrowPanel 2.1" ESP32 Rotary
 * Display (spec §9.1, §9.5).
 *
 * Follows esp-bsp's conventions and API names deliberately. esp_bsp_generic cannot cover
 * this board — it supports SPI displays only, and this panel is an ST7701 over 16-bit RGB
 * parallel — so this is a real BSP component. Keeping the names identical to esp-bsp's is
 * what makes swapping in the M5Dial's official BSP a component-dependency change rather
 * than an edit to crowsnest_ui.
 *
 * Above this header, Crowsnest never mentions a pin number.
 */

#pragma once

#include "driver/i2c_master.h"
#include "esp_err.h"
#include "esp_lcd_types.h"
#include "freertos/FreeRTOS.h"
#include "lvgl.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ---- Board reference (spec Appendix B) --------------------------------------------- */

#define BSP_I2C_SDA (GPIO_NUM_38)
#define BSP_I2C_SCL (GPIO_NUM_39)
#define BSP_I2C_ADDR_EXPANDER (0x21)
#define BSP_I2C_ADDR_TOUCH (0x15)

#define BSP_ENCODER_A (GPIO_NUM_42)
#define BSP_ENCODER_B (GPIO_NUM_4)
#define BSP_LCD_BACKLIGHT (GPIO_NUM_6)

/* ST7701 configuration bus: 3-wire 9-bit SPI, bit-banged (see bsp_crowpanel.c). */
#define BSP_LCD_SPI_CS (GPIO_NUM_16)
#define BSP_LCD_SPI_SCK (GPIO_NUM_2)
#define BSP_LCD_SPI_SDA (GPIO_NUM_1)

#define BSP_LCD_H_RES (480)
#define BSP_LCD_V_RES (480)

/* PCF8574 bit positions at 0x21. P1 and P6-P7 are unused on this board. */
#define BSP_EXP_TOUCH_RESET (1 << 0)
#define BSP_EXP_TOUCH_INT (1 << 2)
#define BSP_EXP_LCD_POWER (1 << 3)
#define BSP_EXP_LCD_RESET (1 << 4)
#define BSP_EXP_ENCODER_BUTTON (1 << 5)

/* ---- I2C ---------------------------------------------------------------------------- */

esp_err_t bsp_i2c_init(void);

i2c_master_bus_handle_t bsp_i2c_get_handle(void);

/*
 * EVERY I2C transaction on this board goes through this mutex, without exception.
 *
 * There is an open esp-bsp issue where M5Dial encoder and button polling collides with
 * LVGL's touch reads and aborts the program. The CrowPanel has the same hazard by
 * construction: the touch controller and the knob button hang off the same bus, polled
 * from different tasks. Serialising from day one is cheaper than debugging sporadic
 * aborts later (spec §9.2).
 */
bool bsp_i2c_lock(uint32_t timeout_ms);
void bsp_i2c_unlock(void);

/* PCF8574 expander. Both take the I2C lock themselves — do not hold it across a call. */
esp_err_t bsp_expander_set(uint8_t mask, bool level);
esp_err_t bsp_expander_read(uint8_t *value);

/* ---- Display ------------------------------------------------------------------------ */

/*
 * Brings up the panel and starts LVGL: expander reset sequence, ST7701 init over 3-wire
 * SPI, RGB peripheral, framebuffer in PSRAM, then esp_lvgl_port.
 * Returns NULL on failure; the caller has no display and should say so on the link.
 */
lv_display_t *bsp_display_start(void);

/* Held for the duration of any LVGL call from outside the LVGL task. */
bool bsp_display_lock(uint32_t timeout_ms);
void bsp_display_unlock(void);

esp_err_t bsp_display_backlight_on(void);
esp_err_t bsp_display_backlight_off(void);

/* 0-100. Brightness policy (day/night) belongs to Crowsnest, not to the BSP. */
esp_err_t bsp_display_brightness_set(int percent);

#ifdef __cplusplus
}
#endif
