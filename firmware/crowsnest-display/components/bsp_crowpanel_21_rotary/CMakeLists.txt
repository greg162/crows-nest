# lvgl and esp_lvgl_port are PUBLIC requirements: bsp/esp-bsp.h returns an lv_display_t,
# so anything including it needs LVGL's headers too.
idf_component_register(
    SRCS "bsp_crowpanel.c"
    INCLUDE_DIRS "include"
    REQUIRES driver esp_lcd esp_driver_i2c esp_driver_ledc esp_driver_gpio
             lvgl__lvgl espressif__esp_lvgl_port)
