# Steamy additions to BetterSteamTools; GPL-3.0, see repository LICENSE.
find_package(Threads REQUIRED)
add_library(OSTPlatform STATIC
    ../Numbers.cpp ../Log.cpp Encoding.cpp DynamicLibrary.cpp
    DirectoryWatch.cpp ByteSearch.cpp Thread.cpp)
target_include_directories(OSTPlatform PUBLIC ..)
target_link_libraries(OSTPlatform PUBLIC Threads::Threads ${CMAKE_DL_LIBS})
set_target_properties(OSTPlatform PROPERTIES POSITION_INDEPENDENT_CODE ON)
target_compile_options(OSTPlatform PRIVATE -Wall -Wextra -Wpedantic)
if(BUILD_TESTING)
    add_executable(ost-platform-tests PlatformTests.cpp)
    target_link_libraries(ost-platform-tests PRIVATE OSTPlatform)
    add_test(NAME ost-platform-conformance COMMAND ost-platform-tests)
    set_tests_properties(ost-platform-conformance PROPERTIES TIMEOUT 15)
endif()
