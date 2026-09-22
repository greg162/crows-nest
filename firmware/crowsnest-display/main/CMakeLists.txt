idf_component_register(
    SRCS "app_main.c"
    INCLUDE_DIRS "."
    REQUIRES bsp_crowpanel_21_rotary crowsnest_input crowsnest_link crowsnest_ui
             esp_driver_usb_serial_jtag esp_hw_support)
