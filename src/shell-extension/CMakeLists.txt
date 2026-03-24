cmake_minimum_required(VERSION 3.20)
project(InstantFileShareShell LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(instant_file_share_shell WIN32
    src/main.cpp
)

target_compile_definitions(instant_file_share_shell PRIVATE UNICODE _UNICODE NOMINMAX)
target_link_libraries(instant_file_share_shell PRIVATE user32)
