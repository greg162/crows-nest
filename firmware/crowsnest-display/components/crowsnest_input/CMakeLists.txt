# The one per-board file besides the BSP (spec §9.2). A board port replaces this
# implementation and nothing above it changes.
idf_component_register(
    SRCS "crowsnest_input.c"
    INCLUDE_DIRS "include"
    REQUIRES bsp_crowpanel_21_rotary esp_driver_pcnt)
