cmake_minimum_required(VERSION 3.20)
project(auto_scale_forwarder_tests LANGUAGES CXX)

set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib)

add_library(FakeLosslessOriginal SHARED src/fake_original.cpp src/common.cpp)
target_compile_features(FakeLosslessOriginal PRIVATE cxx_std_20)
target_compile_definitions(FakeLosslessOriginal PRIVATE WIN32_LEAN_AND_MEAN UNICODE _UNICODE NOMINMAX)
target_link_libraries(FakeLosslessOriginal PRIVATE user32)
set_target_properties(FakeLosslessOriginal PROPERTIES OUTPUT_NAME Lossless_original)

add_executable(FakeBridge src/fake_bridge.cpp src/common.cpp)
target_compile_features(FakeBridge PRIVATE cxx_std_20)
target_compile_definitions(FakeBridge PRIVATE WIN32_LEAN_AND_MEAN UNICODE _UNICODE NOMINMAX)
target_link_libraries(FakeBridge PRIVATE user32)

add_executable(AutoScaleHarness src/harness.cpp src/common.cpp)
target_compile_features(AutoScaleHarness PRIVATE cxx_std_20)
target_compile_definitions(AutoScaleHarness PRIVATE WIN32_LEAN_AND_MEAN UNICODE _UNICODE NOMINMAX)
target_link_libraries(AutoScaleHarness PRIVATE user32)
