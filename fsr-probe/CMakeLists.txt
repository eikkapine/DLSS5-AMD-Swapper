cmake_minimum_required(VERSION 3.20)
project(dlssnr_fsr_probe LANGUAGES CXX)

add_executable(dlssnr_fsr_probe
    src/main.cpp
)

target_compile_features(dlssnr_fsr_probe PRIVATE cxx_std_20)
target_compile_definitions(dlssnr_fsr_probe PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

target_include_directories(dlssnr_fsr_probe PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/third_party/FidelityFX-SDK/Kits/FidelityFX/api/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/third_party/FidelityFX-SDK/Kits/FidelityFX/upscalers/include"
)

target_link_libraries(dlssnr_fsr_probe PRIVATE
    d3d12
    dxgi
    psapi
)

if (MSVC)
    target_compile_options(dlssnr_fsr_probe PRIVATE /W4 /permissive- /EHsc)
endif()
