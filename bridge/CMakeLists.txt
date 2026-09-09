cmake_minimum_required(VERSION 3.20)
project(dlssnr_bridge LANGUAGES CXX)

# Compile production shaders with the installed Windows SDK. There is no
# runtime shader compiler dependency and no GPU execution during this build.
get_filename_component(BRIDGE_WINDOWS_KITS_ROOT
    "[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows Kits\\Installed Roots;KitsRoot10]"
    ABSOLUTE)
find_program(BRIDGE_FXC NAMES fxc
    HINTS
        "$ENV{WindowsSdkVerBinPath}/x64"
        "${BRIDGE_WINDOWS_KITS_ROOT}/bin/${CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION}/x64")
if (NOT BRIDGE_FXC)
    file(GLOB BRIDGE_FXC_CANDIDATES "${BRIDGE_WINDOWS_KITS_ROOT}/bin/*/x64/fxc.exe")
    list(SORT BRIDGE_FXC_CANDIDATES COMPARE NATURAL ORDER DESCENDING)
    if (BRIDGE_FXC_CANDIDATES)
        list(GET BRIDGE_FXC_CANDIDATES 0 BRIDGE_FXC_SELECTED)
        set(BRIDGE_FXC "${BRIDGE_FXC_SELECTED}" CACHE FILEPATH "Windows SDK shader compiler" FORCE)
    endif()
endif()
if (NOT BRIDGE_FXC)
    message(FATAL_ERROR "Windows SDK fxc.exe is required to build the bridge shaders")
endif()

set(BRIDGE_SHADER_DIR "${CMAKE_CURRENT_BINARY_DIR}/generated")
file(MAKE_DIRECTORY "${BRIDGE_SHADER_DIR}")
function(bridge_compile_shader entry profile symbol)
    set(output "${BRIDGE_SHADER_DIR}/${symbol}.h")
    set(compute_define BRIDGE_COMPUTE=0)
    if (profile STREQUAL "cs_5_0")
        set(compute_define BRIDGE_COMPUTE=1)
    endif()
    add_custom_command(OUTPUT "${output}"
        COMMAND "${BRIDGE_FXC}" /nologo /Ges /WX /O3 /T "${profile}" /E "${entry}"
                /D "${compute_define}"
                /Vn "${symbol}" /Fh "${output}"
                "${CMAKE_CURRENT_SOURCE_DIR}/shaders/gpu_transport.hlsl"
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/shaders/gpu_transport.hlsl"
        VERBATIM)
    set(BRIDGE_SHADER_HEADERS ${BRIDGE_SHADER_HEADERS} "${output}" PARENT_SCOPE)
endfunction()
bridge_compile_shader(FullscreenVS vs_5_0 kBridgeFullscreenVS)
bridge_compile_shader(CopyPS ps_5_0 kBridgeCopyPS)
bridge_compile_shader(ScalePS ps_5_0 kBridgeScalePS)
bridge_compile_shader(OpaquePS ps_5_0 kBridgeOpaquePS)
bridge_compile_shader(ComposeCS cs_5_0 kBridgeComposeCS)
bridge_compile_shader(ReduceStatisticsCS cs_5_0 kBridgeReduceStatisticsCS)
add_custom_target(BridgeShaders DEPENDS ${BRIDGE_SHADER_HEADERS})

add_executable(DlssNrBridge
    src/main.cpp
    src/hip_host_timing.cpp
)
add_dependencies(DlssNrBridge BridgeShaders)
target_include_directories(DlssNrBridge PRIVATE "${BRIDGE_SHADER_DIR}")

# Optional declarations only; never link or load a different HIP runtime.
# Supply BRIDGE_HIP_INCLUDE_DIR for SDKs installed through a Python package.
find_path(BRIDGE_HIP_INCLUDE_DIR hip/hip_runtime_api.h
    HINTS "$ENV{HIP_PATH}/include" "$ENV{ROCM_PATH}/include")
if (BRIDGE_HIP_INCLUDE_DIR AND EXISTS "${BRIDGE_HIP_INCLUDE_DIR}/hip/hip_version.h")
    file(STRINGS "${BRIDGE_HIP_INCLUDE_DIR}/hip/hip_version.h" BRIDGE_HIP7_VERSION
        REGEX "^#define HIP_VERSION_MAJOR 7$")
endif()
if (BRIDGE_HIP7_VERSION)
    set_source_files_properties(src/hip_host_timing.cpp PROPERTIES
        INCLUDE_DIRECTORIES "${BRIDGE_HIP_INCLUDE_DIR}"
        COMPILE_DEFINITIONS "BRIDGE_HAVE_HIP_HEADERS=1;__HIP_PLATFORM_AMD__=1")
else()
    message(STATUS "HIP 7 headers not found: host API timing is unavailable in this build")
endif()

target_compile_features(DlssNrBridge PRIVATE cxx_std_20)
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/../VERSION")
file(STRINGS "${CMAKE_CURRENT_SOURCE_DIR}/../VERSION" BRIDGE_BUILD_VERSION LIMIT_COUNT 1)
target_compile_definitions(DlssNrBridge PRIVATE
    WIN32_LEAN_AND_MEAN
    UNICODE
    _UNICODE
    NOMINMAX
    BRIDGE_BUILD_VERSION="${BRIDGE_BUILD_VERSION}"
)

target_link_libraries(DlssNrBridge PRIVATE
    d3d11
    d3d12
    dxgi
    windowsapp
    psapi
    winmm
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
    target_sources(BridgePixelTests PRIVATE src/hip_host_timing.cpp)
    add_dependencies(BridgePixelTests BridgeShaders)
    target_include_directories(BridgePixelTests PRIVATE "${BRIDGE_SHADER_DIR}")
    target_compile_features(BridgePixelTests PRIVATE cxx_std_20)
    target_compile_definitions(BridgePixelTests PRIVATE WIN32_LEAN_AND_MEAN UNICODE _UNICODE NOMINMAX
        BRIDGE_BUILD_VERSION="${BRIDGE_BUILD_VERSION}")
    target_link_libraries(BridgePixelTests PRIVATE d3d11 d3d12 dxgi windowsapp psapi winmm)
    if (MSVC)
        target_compile_options(BridgePixelTests PRIVATE /W4 /permissive- /EHsc /await /arch:AVX2)
    endif()
    add_test(NAME BridgePixelTests COMMAND BridgePixelTests)
endif()
