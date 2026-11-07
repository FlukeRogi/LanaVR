cmake_minimum_required(VERSION 3.20)
project(LanaVRNative CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
set(XR "${CMAKE_CURRENT_SOURCE_DIR}/../tools/openxr")

add_library(LanaVRNative SHARED LanaVRNative.cpp Skybox.cpp)
target_include_directories(LanaVRNative PRIVATE "${XR}/include")
target_link_libraries(LanaVRNative PRIVATE "${XR}/native/x64/release/lib/openxr_loader.lib" d3d11 dxgi windowscodecs ole32)

add_library(LanaMockXR SHARED mock/MockRuntime.cpp)
target_include_directories(LanaMockXR PRIVATE "${XR}/include")
target_link_libraries(LanaMockXR PRIVATE d3d11 dxgi winmm)

add_executable(prof ../tools/prof/prof.cpp)

