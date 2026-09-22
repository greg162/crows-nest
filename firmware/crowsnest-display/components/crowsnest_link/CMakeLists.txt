# No REQUIRES, deliberately. crowsnest_link depends on nothing but libc so it builds and
# runs under Unity on the IDF `linux` target (spec §11.1).
idf_component_register(
    SRCS "crowsnest_link.c"
    INCLUDE_DIRS "include")
