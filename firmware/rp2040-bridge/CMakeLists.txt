# == DO NOT EDIT THE FOLLOWING LINES for the Raspberry Pi Pico VS Code Extension to work ==
if(WIN32)
    set(USERHOME $ENV{USERPROFILE})
else()
    set(USERHOME $ENV{HOME})
endif()
set(sdkVersion 2.2.0)
set(toolchainVersion 14_2_Rel1)
set(picotoolVersion 2.2.0-a4)
set(picoVscode ${USERHOME}/.pico-sdk/cmake/pico-vscode.cmake)
if (EXISTS ${picoVscode})
    include(${picoVscode})
endif()
# ====================================================================================
cmake_minimum_required(VERSION 3.13)

# ── RP2040 Zero HID Bridge ────────────────────────────────────────────────
# Faz 1: composite HID (klavye + fare + vendor). USB-CDC kaldırıldı.
#
# Derleme: VS Code "Compile" düğmesi  YA DA komut satırı:
#   cmake --build build
# ---------------------------------------------------------------------------

set(PICO_BOARD pico CACHE STRING "Board type")

include(pico_sdk_import.cmake)

project(rp2040_bridge C CXX ASM)

set(CMAKE_C_STANDARD   11)
set(CMAKE_CXX_STANDARD 17)

pico_sdk_init()

add_executable(rp2040_bridge
    src/main.c
    src/usb_descriptors.c
)

# tusb_config.h + protocol.h burada
target_include_directories(rp2040_bridge PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}/src
)

target_link_libraries(rp2040_bridge
    pico_stdlib
    tinyusb_device
    hardware_pio
)

# WS2812 RGB LED (GPIO16) için PIO header üret
pico_generate_pio_header(rp2040_bridge ${CMAKE_CURRENT_LIST_DIR}/src/ws2812.pio)

# Faz 1: CDC/stdio yok — cihaz custom composite HID.
# .uf2 / .hex / .bin / .map üret
pico_add_extra_outputs(rp2040_bridge)
