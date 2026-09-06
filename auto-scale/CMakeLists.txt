cmake_minimum_required(VERSION 3.20)

project(NrAutoScaleProxy LANGUAGES CXX)

add_library(Lossless SHARED
    src/auto_scale_proxy.cpp
)

target_compile_features(Lossless PRIVATE cxx_std_20)
target_compile_definitions(Lossless PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    UNICODE
    _UNICODE
)

if(MSVC)
    target_compile_options(Lossless PRIVATE /W4 /permissive- /EHsc)
else()
    target_compile_options(Lossless PRIVATE -Wall -Wextra -Wpedantic)
endif()

target_link_libraries(Lossless PRIVATE user32 shell32)

set_target_properties(Lossless PROPERTIES
    OUTPUT_NAME "Lossless"
)
