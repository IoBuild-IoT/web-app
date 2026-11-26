# Hardware real (nodo ESP32)

El firmware conectado está en `hardware/firmware/iobuild_edge_node/` (ver al final). El repositorio **IoBuild-IoT/edge-node** guarda las versiones anteriores.

Cambios de este repositorio que lo hacen posible:

- `iot-simulator/simulator.py`: la variable `HARDWARE_DEVICE_IDS` (IDs separados por coma) hace que el simulador ignore los dispositivos atendidos por el ESP32, para que no publique telemetría falsa sobre ellos.
- `docker-compose.yml` y `docker-compose.dokploy.yml`: pasan `HARDWARE_DEVICE_IDS` al simulador.
- `iot-monitor/monitor.py`: muestra registro, órdenes y confirmaciones MQTT en `8090`.
- El ESP32 usa el mismo contrato MQTT que el simulador (`telemetry/{id}` y `commands/{id}`). El backend agrega el tipo `EdgeNode` (umbrales) y devuelve `node` en `/devices/{id}/status`.

## Antes de desplegar

1. Cree los dispositivos reales desde la aplicación y anote el ID asignado a cada uno.
2. Configure `HARDWARE_DEVICE_IDS` con esos IDs, por ejemplo `HARDWARE_DEVICE_IDS=7,8`.
3. Use un nombre de host o IP pública del VPS como `MQTT_HOST` en el firmware.
4. En producción, configure `MQTT_PASSWORD` en Dokploy y abra el puerto `1883/TCP`.
5. Use credenciales MQTT diferentes a las credenciales de usuario de IoBuild.

## Contrato mínimo

Publicación del ESP32:

```json
{
  "deviceId": 8,
  "eventId": "esp32-8-1795286335-27",
  "timestamp": "2026-11-21T18:38:55.079Z",
  "status": "online",
  "reported": {"power":true,"mode":"cooling","targetTemperature":24},
  "energy_kwh": 0.0017,
  "temperature_c": 25.0,
  "voltage_v": 220,
  "node": {"humidity":55,"presence":false,"blind_deg":180,"alarm":false}
}
```

El ESP32 publica en `telemetry/{id}` y recibe órdenes retenidas de QoS 1 en
`commands/{id}`. Para confirmar una orden, devuelva el mismo estado completo en
`reported`; el backend reconoce la orden por equivalencia JSON.

Campos comunes:

- `deviceId`: debe coincidir con el ID registrado en IoBuild.
- `eventId`: identificador único de cada lectura.
- `timestamp`: fecha ISO 8601.
- `status`: estado informado por el nodo.
- `reported`: atributos confirmados por el nodo.
- `energy_kwh`, `temperature_c`, `voltage_v`: mediciones numéricas principales.
- `node`: opcional; admite `humidity`, `presence`, `blind_deg` y `alarm`.

## Validación local

```bash
python -m unittest discover -s iot-simulator -p "test_*.py"
python -m unittest discover -s iot-monitor -p "test_*.py"
docker compose --profile telemetry up -d --wait
curl -f http://localhost:8090/api/state
```

Prueba del filtro del simulador:

```bash
python -m unittest discover -s iot-simulator -p "test_*.py"
```

## Umbrales configurables desde el front (tipo `EdgeNode`)

El Owner crea en su unidad un dispositivo **Edge Node (ESP32)**. Ese ID es el
tercer ID del firmware (`ID_NODO`), junto a `ID_LUZ` e `ID_CLIMA`; agréguelo
también a `HARDWARE_DEVICE_IDS`. El panel de control muestra un slider por umbral:

| Atributo | Rango | Paso | Por defecto (firmware) | Uso en el firmware |
|---|---|---|---|---|
| `lightHoldSeconds` | 5–600 s | 5 | 10 | `HOLD_MS = v * 1000` |
| `climateDelta` | 0.5–5 °C | 0.5 | 1.5 | clima enciende a ambiente + v |
| `alarmDelta` | 1–10 °C | 0.5 | 3 | alarma a ambiente + v (se libera a v − 1) |
| `blindStep` | 0.5–5 °C | 0.5 | 1 | `PASO_SERVO = v` |
| `alarmEnabled` | true/false | — | true | desactiva buzzer y LED rápido |

El backend valida rangos (400 si se salen) y publica en `commands/{ID_NODO}`
(QoS 1, retenido) el **estado deseado completo acumulado**, por ejemplo
`{"climateDelta":2.5,"lightHoldSeconds":30,"alarmEnabled":false}`. Solo vienen
las claves que el Owner cambió alguna vez; las demás conservan su valor actual.

Para confirmar, el nodo publica en `telemetry/{ID_NODO}` con `reported` igual a
ese JSON y un `timestamp` con milisegundos (requiere NTP): el backend ignora
confirmaciones con hora anterior a la orden. Si el reloj atrasa, la siguiente
telemetría periódica (cada 5 s) confirma igual.

Referencia para el firmware (ArduinoJson 7 + `Preferences` para NVS):

```cpp
#include <Preferences.h>
Preferences prefs;
JsonDocument deseadoNodo;  // último estado deseado recibido, se repite en `reported`

void cargarUmbrales() {               // llamar en setup()
  prefs.begin("iobuild", true);
  HOLD_MS        = prefs.getFloat("hold", 10) * 1000;
  DELTA_CLIMA    = prefs.getFloat("clima", 1.5);
  DELTA_ALARMA   = prefs.getFloat("alarma", 3);
  PASO_SERVO     = prefs.getFloat("paso", 1);
  ALARMA_ACTIVA  = prefs.getBool("alarmOn", true);
  String json    = prefs.getString("deseado", "{}");
  prefs.end();
  deserializeJson(deseadoNodo, json);
}

void aplicarUmbrales(JsonObjectConst d) {   // desde el callback de commands/ID_NODO
  prefs.begin("iobuild", false);
  if (d["lightHoldSeconds"].is<float>()) { HOLD_MS = d["lightHoldSeconds"].as<float>() * 1000; prefs.putFloat("hold", HOLD_MS / 1000.0); }
  if (d["climateDelta"].is<float>())     { DELTA_CLIMA = d["climateDelta"];   prefs.putFloat("clima", DELTA_CLIMA); }
  if (d["alarmDelta"].is<float>())       { DELTA_ALARMA = d["alarmDelta"];    prefs.putFloat("alarma", DELTA_ALARMA); }
  if (d["blindStep"].is<float>())        { PASO_SERVO = d["blindStep"];       prefs.putFloat("paso", PASO_SERVO); }
  if (d["alarmEnabled"].is<bool>())      { ALARMA_ACTIVA = d["alarmEnabled"]; prefs.putBool("alarmOn", ALARMA_ACTIVA); }
  deseadoNodo.set(d);
  String json; serializeJson(deseadoNodo, json);
  prefs.putString("deseado", json);
  prefs.end();
  publicarTelemetria(ID_NODO, deseadoNodo);  // `reported` = deseadoNodo → confirma la orden
}
```

Pruebas de esta parte:

```bash
bash backend/tests/Integration/run_edge_node_settings_e2e.sh
bash backend/tests/Integration/run_esp32_node_e2e.sh
```

## Firmware en este repo (`hardware/firmware/iobuild_edge_node/`)

1. Copie `secrets.example.h` como `secrets.h` (ya está en `.gitignore`) y complete el WiFi (nombre de la red 2.4 GHz, no la MAC), `MQTT_HOST` e IDs.
2. `MQTT_HOST` = IP de la PC con docker compose (`ipconfig getifaddr en0` en Mac). **El router puede cambiarla**: si el monitor serie muestra `[MQTT] sin conexión (estado -2)`, revise la IP o reserve una IP fija en el router.
3. Compilar y subir (Arduino IDE, o la CLI que trae el IDE):

```bash
arduino-cli compile --fqbn esp32:esp32:esp32 hardware/firmware/iobuild_edge_node
arduino-cli upload -p /dev/cu.usbserial-0001 --fqbn esp32:esp32:esp32 hardware/firmware/iobuild_edge_node
```

4. Monitor serie a 115200: debe verse `[MQTT] conectado ...` y `PLATAFORMA: CONECTADO`.

Validado el 2026-11-21 con la placa real: telemetría de los 3 dispositivos en el dashboard (humedad, presencia, persiana, alarma) y órdenes desde el front (`climateDelta`, luz On/Off) confirmadas por la placa.

## Compilar el firmware con Docker (sin Arduino IDE)

`hardware/firmware/Dockerfile` trae arduino-cli 1.6.0, el core esp32 4.0.0 y las cuatro
librerías con las versiones validadas en la placa. Necesita `secrets.h` (no está en git).

```bash
docker compose -f hardware/firmware/docker-compose.yml run --rm firmware
```

Si la ruta de la carpeta contiene `:` (por ejemplo `P:` en una carpeta de Google Drive),
Compose falla con `invalid volume specification`. Usa entonces `docker run`:

```bash
cd hardware/firmware && docker build -t iobuild-edge-node-firmware . && mkdir -p build
```

```bash
docker run --rm --mount "type=bind,source=$PWD/iobuild_edge_node,target=/sketch,readonly" --mount "type=bind,source=$PWD/build,target=/out" iobuild-edge-node-firmware
```

Deja los binarios en `hardware/firmware/build/` (también en `.gitignore`). El archivo para
flashear de una vez es `iobuild_edge_node.ino.merged.bin`; flashearlo **borra los ajustes
guardados en la placa** (vuelven solos desde la plataforma al conectarse).

La imagen pesa ~2.4 GB y solo compila para el ESP32 clásico: `trim_index.py` excluye el
toolchain RISC-V, las librerías de otros chips y los depuradores. Para S2/S3/C3/C6, quita
ese paso del `Dockerfile`.

Flashear:

- **macOS / Windows:** Docker Desktop no ve el USB. Flashea desde el host con `esptool`
  (viene con el Arduino IDE o con `pip install esptool`):

  ```bash
  esptool --chip esp32 --port /dev/cu.usbserial-0001 write-flash 0x0 hardware/firmware/build/iobuild_edge_node.ino.merged.bin
  ```

- **Linux:** el contenedor puede subirlo directo:

  ```bash
  docker compose -f hardware/firmware/docker-compose.yml run --rm --device /dev/ttyUSB0 firmware flash /dev/ttyUSB0
  ```
