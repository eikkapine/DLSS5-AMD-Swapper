cmake_minimum_required(VERSION 3.20)
project(dlssnr_bridge LANGUAGES CXX)

add_executable(DlssNrBridge
    src/main.cpp
)

target_compile_features(DlssNrBridge PRIVATE cxx_std_20)
target_compile_definitions(DlssNrBridge PRIVATE
    WIN32_LEAN_AND_MEAN
    UNICODE
    _UNICODE
    NOMINMAX
)

target_link_libraries(DlssNrBridge PRIVATE
    d3d11
    d3d12
    dxgi
    windowsapp
    psapi
)

if (MSVC)
    target_compile_options(DlssNrBridge PRIVATE /W4 /permissive- /EHsc /await)
endif()
