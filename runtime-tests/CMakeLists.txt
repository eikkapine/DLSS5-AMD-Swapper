cmake_minimum_required(VERSION 3.20)
project(swapper_runtime_tests LANGUAGES CXX)
if(NOT WIN32)
  message(FATAL_ERROR "These probes require Windows Direct3D runtimes.")
endif()

add_executable(swapper_d3d11_probe src/d3d11_probe.cpp)
target_compile_features(swapper_d3d11_probe PRIVATE cxx_std_20)
target_compile_definitions(swapper_d3d11_probe PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(swapper_d3d11_probe PRIVATE d3d11 dxgi d3dcompiler psapi)
if(MSVC)
  target_compile_options(swapper_d3d11_probe PRIVATE /W4 /permissive- /EHsc)
endif()

# Reuse the real D3D12/FFX temporal-input probe instead of inventing an installer fixture.
add_subdirectory(../fsr-probe fsr-probe)
