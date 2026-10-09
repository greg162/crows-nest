idf_component_register(
    SRCS "crowsnest_ui.c"
         "ui_frame.c"
         "ui_value.c"
         "fonts/ui_font_value_72.c"
         "views/framed_value.c"
         "views/radio_pair.c"
         "views/value_stack.c"
         "views/waiting.c"
    INCLUDE_DIRS "include"
    PRIV_INCLUDE_DIRS "."
    REQUIRES crowsnest_link)
