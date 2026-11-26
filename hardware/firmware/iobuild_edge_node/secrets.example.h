// Copia este archivo como secrets.h y completa los valores. secrets.h NO se sube a git.
#pragma once

// WiFi 2.4 GHz (el ESP32 no ve redes de 5 GHz ni portales cautivos)
#define WIFI_SSID "tu-red"
#define WIFI_PASS "tu-clave"

// Broker MQTT del backend de IoBuild
//  - Local (docker compose en tu PC): IP de tu PC en la red, ej. "192.168.1.59", sin usuario/clave
//  - VPS (Dokploy): host público, MQTT_USER "iobuild" y MQTT_PASS = MQTT_PASSWORD de Dokploy
#define MQTT_HOST "192.168.1.59"
#define MQTT_PORT 1883
#define MQTT_USER ""
#define MQTT_PASS ""

// IDs que la app asignó a cada dispositivo (Gestión de dispositivos)
#define ID_LUZ   0   // Smart Light      -> relé 1 (luz pasillo)
#define ID_CLIMA 0   // Air Conditioner  -> relé 2 (clima)
#define ID_NODO  0   // Edge Node (ESP32) -> umbrales configurables desde el front

// Tus relés físicos se activan con LOW (en Wokwi: false)
#define RELE_ACTIVO_BAJO true
