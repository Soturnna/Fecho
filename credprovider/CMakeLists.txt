cmake_minimum_required(VERSION 3.20)
project(FechoCredentialProvider CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(FechoCredentialProvider SHARED
    Dll.cpp
    Guid.cpp
    FechoProvider.cpp
    FechoCredential.cpp
    CredPacking.cpp
    UnlockState.cpp
    Fecho.def
)

set_target_properties(FechoCredentialProvider PROPERTIES OUTPUT_NAME "FechoCredentialProvider")

target_compile_definitions(FechoCredentialProvider PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN)

target_link_libraries(FechoCredentialProvider PRIVATE
    secur32
    advapi32
    shlwapi
    shell32
    crypt32
    ole32
)
