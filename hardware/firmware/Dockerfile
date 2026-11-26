# Compila el firmware del ESP32 sin Arduino IDE.
# Versiones fijadas: las mismas del Arduino IDE con el que se validó la placa.
FROM debian:bookworm-slim

ARG ARDUINO_CLI_VERSION=1.6.0
ARG ESP32_CORE_VERSION=4.0.0
ARG TARGETARCH

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl python3 \
 && rm -rf /var/lib/apt/lists/*

RUN case "$TARGETARCH" in arm64) arch=ARM64 ;; *) arch=64bit ;; esac \
 && curl -fsSL "https://downloads.arduino.cc/arduino-cli/arduino-cli_${ARDUINO_CLI_VERSION}_Linux_${arch}.tar.gz" \
    | tar -xz -C /usr/local/bin arduino-cli

# Índice recortado: solo las herramientas del ESP32 clásico (ver trim_index.py).
COPY trim_index.py /opt/
RUN curl -fsSL https://espressif.github.io/arduino-esp32/package_esp32_index.json -o /tmp/index.json \
 && python3 /opt/trim_index.py /tmp/index.json /opt/package_esp32_index.json "${ESP32_CORE_VERSION}" \
 && rm /tmp/index.json

# Toolchains pesan cientos de MB: timeout amplio y hasta 3 intentos por si la red es lenta.
RUN --mount=type=cache,target=/root/.arduino15/staging \
    arduino-cli config init \
 && arduino-cli config set network.connection_timeout 600s \
 && arduino-cli config add board_manager.additional_urls file:///opt/package_esp32_index.json \
 && arduino-cli core update-index \
 && for try in 1 2 3; do arduino-cli core install "esp32:esp32@${ESP32_CORE_VERSION}" && break; [ "$try" = 3 ] && exit 1; sleep 5; done \
 && arduino-cli lib install "DHT sensor library@1.4.7" "Adafruit Unified Sensor@1.1.15" "PubSubClient@2.8" "ArduinoJson@7.4.3"

COPY entrypoint.sh /usr/local/bin/firmware
RUN chmod +x /usr/local/bin/firmware
ENTRYPOINT ["firmware"]
CMD ["compile"]
