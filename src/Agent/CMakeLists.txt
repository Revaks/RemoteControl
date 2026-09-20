cmake_minimum_required(VERSION 3.20)
project(RemoteControlAgent C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

include(FetchContent)

# LibVNCServer ищет внешние библиотеки (OpenSSL, GnuTLS, SASL, zlib, jpeg, png, ssh2...).
# Если на машине сборки они найдутся, агент получит зависимость от чужих DLL — и на целевой
# машине служба не стартует (MSI: Error 1920). Агенту это не нужно: наружный TLS делает
# Schannel, а консоль использует Raw/CopyRect/Hextile. Поэтому фиксируем всё выключенным —
# сборка одинаковая на любой машине.
foreach(_vnc_opt
        WITH_OPENSSL WITH_GNUTLS WITH_GCRYPT WITH_SASL WITH_LIBSSH2 WITH_WEBSOCKETS
        WITH_ZLIB WITH_LZO WITH_JPEG WITH_PNG WITH_SDL WITH_GTK WITH_SYSTEMD
        WITH_FFMPEG WITH_TESTS WITH_EXAMPLES)
    set(${_vnc_opt} OFF CACHE BOOL "off: агент не использует внешние зависимости LibVNCServer" FORCE)
endforeach()

# LibVNCServer (LGPL-2.1). Можно указать локальный исходник:
#   cmake -DLIBVNCSERVER_DIR=third_party/libvncserver ...
# иначе подтянется из GitHub (тег LibVNCServer-0.9.14).
set(LIBVNCSERVER_DIR "" CACHE PATH "Path to LibVNCServer source tree")
if(LIBVNCSERVER_DIR)
    add_subdirectory(${LIBVNCSERVER_DIR} ${CMAKE_BINARY_DIR}/libvncserver)
else()
    FetchContent_Declare(libvncserver
        GIT_REPOSITORY https://github.com/LibVNC/libvncserver.git
        GIT_TAG        LibVNCServer-0.9.14
        GIT_SHALLOW    TRUE)
    FetchContent_MakeAvailable(libvncserver)
endif()

add_executable(RemoteControlAgent WIN32
    agent_main.c
    service.c
    tls.c
    auth.c
    session.c
    capture.c
    dxgi_capture.c
    input.c
    eventlog.c)

target_compile_definitions(RemoteControlAgent PRIVATE
    _CRT_SECURE_NO_WARNINGS
    _CRT_NONSTDC_NO_DEPRECATE
    WIN32_LEAN_AND_MEAN
    UNICODE
    _UNICODE)

target_link_libraries(RemoteControlAgent PRIVATE
    vncserver
    ws2_32 secur32 crypt32 ncrypt wldap32 netapi32 advapi32 user32 gdi32 wtsapi32
    d3d11 dxgi dxguid)

# Заголовки LibVNCServer: rfb/rfb.h лежит в корне исходников, а сгенерированный
# rfb/rfbconfig.h — в корне бинарного дерева (target_include_directories у
# libvncserver может не пробрасываться в цель родительского проекта).
if(TARGET vncserver)
    get_target_property(_vnc_source_dir vncserver SOURCE_DIR)
    get_target_property(_vnc_binary_dir vncserver BINARY_DIR)
    if(_vnc_source_dir AND _vnc_binary_dir)
        target_include_directories(RemoteControlAgent PRIVATE
            ${_vnc_source_dir}
            ${_vnc_binary_dir})
    endif()
endif()

if(MSVC)
    # /W3 — обычные предупреждения.
    # /utf-8 — все исходники в UTF-8 без BOM; без него MSVC читает их в ANSI-кодировке
    # системы и кириллица в L"..." (сообщения Event Log) попадает искажённой.
    target_compile_options(RemoteControlAgent PRIVATE /W3 /utf-8)
endif()
