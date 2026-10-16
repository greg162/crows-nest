cmake_minimum_required(VERSION 3.16)

# Board selection. A board port is three artefacts (spec §9.3): one BSP component, one
# crowsnest_input implementation, and one sdkconfig defaults file. crowsnest_link and
# crowsnest_ui never change.
#
#   idf.py -DCROWSNEST_BOARD=crowpanel_21 build
if(NOT DEFINED CROWSNEST_BOARD)
    set(CROWSNEST_BOARD "crowpanel_21")
endif()

# A plain set() here would shadow anything passed with -DSDKCONFIG_DEFAULTS, which is how
# the devconsole overlay is applied. Only supply the default when the caller has not.
if(NOT DEFINED SDKCONFIG_DEFAULTS)
    set(SDKCONFIG_DEFAULTS "sdkconfig.defaults;sdkconfig.defaults.${CROWSNEST_BOARD}")
endif()

# The firmware version, the one place it is set. ESP-IDF stores it in the image's app
# header, where the firmware reads it for its hello and the flasher reads it from the .bin.
set(PROJECT_VER "0.4.3")

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(crowsnest-display)
