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
    target_compile_options(DlssNrBridge PRIVATE
        /W4
        /permissive-
        /EHsc
        /await
        $<$<CONFIG:Release>:/O2 /Oi /Ot /GL /arch:AVX2 /fp:fast>
        $<$<CONFIG:RelWithDebInfo>:/O2 /Oi /Ot /GL /arch:AVX2 /fp:fast>
    )
    target_link_options(DlssNrBridge PRIVATE
        $<$<CONFIG:Release>:/LTCG>
        $<$<CONFIG:RelWithDebInfo>:/LTCG>
    )
endif()

include(CTest)
if (BUILD_TESTING)
    add_executable(BridgePixelTests tests/pixel_tests.cpp)
    target_compile_features(BridgePixelTests PRIVATE cxx_std_20)
    target_compile_definitions(BridgePixelTests PRIVATE WIN32_LEAN_AND_MEAN UNICODE _UNICODE NOMINMAX)
    target_link_libraries(BridgePixelTests PRIVATE d3d11 d3d12 dxgi windowsapp psapi)
    if (MSVC)
        target_compile_options(BridgePixelTests PRIVATE /W4 /permissive- /EHsc /await /arch:AVX2)
    endif()
    add_test(NAME BridgePixelTests COMMAND BridgePixelTests)
endif()
