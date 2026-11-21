# Hardware real (nodo ESP32)

El firmware del prototipo físico vive en el repositorio **IoBuild-IoT/edge-node**. Allí está la guía completa para conectarlo a esta plataforma.

Cambios de este repositorio que lo hacen posible:

- `iot-simulator/simulator.py`: la variable `HARDWARE_DEVICE_IDS` (IDs separados por coma) hace que el simulador ignore los dispositivos atendidos por el ESP32, para que no publique telemetría falsa sobre ellos.
- `docker-compose.yml` y `docker-compose.dokploy.yml`: pasan `HARDWARE_DEVICE_IDS` al simulador.
- El backend y el frontend no cambian: el ESP32 usa el mismo contrato MQTT que el simulador (`telemetry/{id}` y `commands/{id}`).

Prueba del filtro: `python -m unittest discover -s iot-simulator -p "test_*.py"`
