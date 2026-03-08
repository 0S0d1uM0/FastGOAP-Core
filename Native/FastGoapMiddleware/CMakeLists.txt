cmake_minimum_required(VERSION 3.20)
project(FastGoapMiddleware LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_library(FastGoapMiddleware SHARED
    src/FastGoapMiddleware.cpp
    src/FastGoapPlanner.cpp
    src/FastGoapRuntime.cpp
)

target_compile_definitions(FastGoapMiddleware PRIVATE FASTGOAP_EXPORTS)

if(MSVC)
    target_compile_options(FastGoapMiddleware PRIVATE /W4 /EHsc)
else()
    target_compile_options(FastGoapMiddleware PRIVATE -Wall -Wextra -Wpedantic)
endif()

set_target_properties(FastGoapMiddleware PROPERTIES
    OUTPUT_NAME "FastGoapMiddleware"
)
