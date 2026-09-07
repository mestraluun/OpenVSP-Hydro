CMAKE_MINIMUM_REQUIRED(VERSION 3.24)

INCLUDE_DIRECTORIES(
        ${CMAKE_CURRENT_SOURCE_DIR}/../../geom_core
)

# Compiled directly rather than linked against geom_core: the solver is deliberately free of
# OpenVSP dependencies, which keeps this test fast and isolates it from the rest of the build.
ADD_EXECUTABLE( fsvlm_test
        fsvlm_test.cpp
        ${CMAKE_CURRENT_SOURCE_DIR}/../../geom_core/FreeSurfaceVLM.cpp
)

ADD_TEST( NAME FreeSurfaceVLM COMMAND fsvlm_test )
