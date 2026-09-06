cmake_minimum_required(VERSION 3.20)
project(dlssnr_d3d12_probe LANGUAGES CXX)

add_executable(dlssnr_d3d12_probe
    src/main.cpp
)

target_compile_features(dlssnr_d3d12_probe PRIVATE cxx_std_20)
target_compile_definitions(dlssnr_d3d12_probe PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

target_link_libraries(dlssnr_d3d12_probe PRIVATE
    d3d12
    dxgi
    psapi
)

if (MSVC)
    target_compile_options(dlssnr_d3d12_probe PRIVATE /W4 /permissive- /EHsc)
endif()
