# Guía: IoBuild + ESP32 (local y en una VM)

Guía paso a paso para encender el prototipo, correr la plataforma en tu PC y
desplegarla en una VM para que el ESP32 se conecte desde cualquier red.

---

## 0. Cómo funciona (en una línea)

```
ESP32 ──WiFi──> Internet ──MQTT :1884──> Mosquitto ──> Backend (.NET) ──> MySQL + InfluxDB ──> Front (Vue)
   ↑                                                     │
   └──────────── órdenes commands/{id} (retenidas) ──────┘
```

El ESP32 **no habla con el front**: habla con el broker MQTT (Mosquitto), que
corre junto al backend. Por eso el ESP32 necesita la IP del lugar donde corre
el backend (tu PC o la VM).

| Dispositivo en la app | ID actual (stack local) | En la placa |
|---|---|---|
| Smart Light | 17 | Relé 1 + LED amarillo (luz pasillo, PIR) |
| Air Conditioner | 16 | Relé 2 + LED azul (clima) |
| Edge Node (ESP32) | 18 | Umbrales, humedad, presencia, persiana, alarma |

> En la VM la base de datos es nueva: los IDs **cambian**. Ver paso 4.5.

---

## 1. Encender el ESP32 (uso diario)

1. **Alimentación**: conecta el ESP32 por USB a la PC o a un cargador de celular (5 V).
   El PIR va a VIN 5 V. Si usas cargador, no hay monitor serie, pero todo funciona igual.
2. **Al encender** (≈ 15 s): autotest. Cada LED/relé prende 2 s, 3 pitidos, el servo
   barre 0→180→0. Si algo no se mueve, revisa ese cable.
3. **Primeros 30 s**: el PIR se calienta (ignora movimiento). No toques el DHT22: toma
   la temperatura ambiente de referencia.
4. **LED verde (D18)**:

   | LED verde | Significa |
   |---|---|
   | Fijo | Todo OK y conectado a la plataforma |
   | Parpadeo lento | Sin plataforma (WiFi/broker) o DHT22 sin lectura |
   | Parpadeo rápido | Alarma de sobretemperatura |

5. **Sin internet** el nodo sigue trabajando con las reglas locales y los últimos
   umbrales guardados (NVS). Reintenta WiFi cada 10 s y MQTT cada 5 s.

### Ver qué hace (monitor serie)

Arduino IDE → Herramientas → Puerto `/dev/cu.usbserial-0001` → Monitor serie a **115200**.
Debes ver:

```
[MQTT] conectado a <host>:1884 (IP nodo 192.168.x.x)
PLATAFORMA: CONECTADO
[MQTT] orden commands/18: {...}        <- cuando cambias algo en la app
[  43s] T=27.2°C HR=78% ... nube=OK
```

### Panel de control del front (Gestión de dispositivos, como Owner)

Cada componente físico tiene su tarjeta. Los interruptores y botones envían al instante;
los sliders envían **al soltarlos** (no hay botón "Send").

| Tarjeta | Componente | Qué controlas |
|---|---|---|
| Luz del pasillo | LED amarillo (relé 1) | Encendido (habilita la luz automática), brillo |
| Clima | LED azul (relé 2) | Encendido, modo Frío/Calor/Ventilador, temperatura objetivo |
| Sensor de movimiento | PIR | Activar/desactivar, tiempo que sigue encendida la luz |
| Persiana | Servo | **Automática** (por temperatura) o **Manual**: Cerrar 0°, Mitad 90°, Abrir 180° o ángulo exacto |
| Alarma | Buzzer | Activar/desactivar, umbral de temperatura |
| Clima automático | Reglas | Cuánto sobre el ambiente enciende el clima |
| LED de estado | LED verde | Solo lectura: fijo, parpadeo rápido (alarma) o lento (sin conexión) |

El chip "ahora" de cada tarjeta muestra el estado **real** de la placa (por ejemplo, si la luz está
encendida en este momento), no solo la orden enviada. Si la última lectura tiene más de 2 minutos,
el dispositivo aparece **Offline**. Para ver los chips y mover el servo real hay que flashear el
firmware nuevo.

---

## 2. Lo que se hizo (resumen)

**Backend**
- Ingesta MQTT: los mensajes inválidos ahora quedan en el log (antes se perdían en silencio);
  `blind_deg` decimal se redondea; si el ESP32 no tiene hora (1970) se usa la del servidor.
- `GET /api/v1/devices/{id}/status` devuelve `node` (humedad, presencia, persiana, alarma).
- Nuevo tipo **EdgeNode** con 5 umbrales configurables y validación de rangos
  (`lightHoldSeconds`, `climateDelta`, `alarmDelta`, `blindStep`, `alarmEnabled`).

**Front**
- Los dashboards (Builder y Owner) muestran humedad, presencia, persiana y alarma.
- Los sliders aceptan decimales (paso 0.5) y arrancan con los valores del firmware.

**Firmware** (`hardware/firmware/iobuild_edge_node/`)
- Tus reglas locales intactas + WiFi + MQTT + hora NTP + órdenes desde la app +
  umbrales guardados en NVS + telemetría cada 5 s + confirmación de órdenes.

**Infraestructura**
- `.env`: se agregaron `MQTT_USERNAME`, `MQTT_PASSWORD`, `HARDWARE_DEVICE_IDS`,
  `INFLUXDB_ADMIN_PASSWORD`, `INFLUXDB_ADMIN_TOKEN`.
- `docker-compose.vm.yml`: override para VM que obliga usuario/clave en el broker.

**Pruebas**: backend 282/282, front 79/79, 2 E2E con Docker, y la placa real
validada: datos en el dashboard y órdenes `climateDelta`, luz On/Off confirmadas.

---

## 3. Correr todo en tu PC (red de casa)

> Docker no acepta rutas con `:` (tu carpeta tiene `P:`). Clona o copia `web-app`
> a una ruta simple, por ejemplo `~/iobuild/web-app`, y copia ahí tu `.env`.

```bash
cd ~/iobuild/web-app
docker compose --profile telemetry up -d --build
```

- Front: http://localhost · API: http://localhost:8080 · Monitor MQTT: http://localhost:8090
- En `secrets.h`: `MQTT_HOST` = IP de tu Mac (`ipconfig getifaddr en0`), `MQTT_USER`/`MQTT_PASS` vacíos.
- Si el router cambia la IP de tu Mac, el ESP32 muestra `estado -2`: actualiza `MQTT_HOST`
  y vuelve a flashear (o reserva una IP fija en el router).

Apagar: `docker compose --profile telemetry down` (los datos se conservan).

---

## 4. Desplegar en una VM (para conectarte desde cualquier red)

### 4.1 Crear la VM

- Proveedor: Azure, AWS, Google Cloud, DigitalOcean, Oracle, etc.
- Sistema: **Ubuntu 22.04 o 24.04**.
- Tamaño: **2 vCPU y 4 GB RAM** mínimo (MySQL + InfluxDB + .NET + build del front). 20 GB de disco.
- Anota la **IP pública** (ej. `20.50.60.70`).

### 4.2 Abrir puertos en el firewall del proveedor (Network Security Group / Security List)

| Puerto | Para qué | ¿Abrir? |
|---|---|---|
| 22/TCP | SSH | Sí (idealmente solo tu IP) |
| 80/TCP | Front + API (nginx) | Sí |
| 1884/TCP | MQTT del ESP32 | Sí |
| 8080/TCP | API directa | Opcional |
| 8090/TCP | Monitor MQTT (sin login) | **No** (úsalo con túnel SSH) |

### 4.3 Instalar Docker en la VM

```bash
ssh usuario@IP_PUBLICA
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER && exit
```

Vuelve a entrar por SSH para que el grupo `docker` tenga efecto.

### 4.4 Subir el proyecto y el `.env`

Opción A (recomendada): sube los cambios a GitHub desde tu PC y clona en la VM.

```bash
# en tu PC, dentro de web-app
git add -A && git commit -m "feat(iot): ESP32 edge node, VM override" && git push
```

```bash
# en la VM
git clone https://github.com/IoBuild-IoT/web-app.git && cd web-app
```

Opción B: copiar la carpeta directo con `rsync`/`scp`, desde tu PC.

El `.env` **no** está en git (tiene secretos): cópialo a mano desde tu PC:

```bash
scp .env usuario@IP_PUBLICA:~/web-app/.env
```

En la VM, revisa el `.env`:
- Hay claves repetidas (`JWT_SECRET`, `DB_PASSWORD`, `STRIPE_SECRET_KEY`,
  `STRIPE_USE_SIMULATED_PAYMENTS`): deja **una sola** de cada una.
- `MQTT_PASSWORD` debe existir: es la clave que usará el ESP32.
- `CORS_ALLOWED_ORIGINS=http://IP_PUBLICA` (o `*` para la demo).
- `HARDWARE_DEVICE_IDS=` vacío por ahora (se llena en 4.6).

### 4.5 Levantar la plataforma en la VM

```bash
cd ~/web-app
docker compose -f docker-compose.yml -f docker-compose.vm.yml --profile telemetry up -d --build
docker compose ps
```

Prueba desde tu navegador: `http://IP_PUBLICA`. Luego, igual que en local:
1. Registra un Builder, elige plan (pagos simulados), crea proyecto con 1 unidad.
2. En Clientes, agrega al Owner con su email y asígnale la unidad.
3. Registra al Owner con ese email; en Gestión de dispositivos agrega **Edge Node (ESP32)**.
4. Anota los IDs nuevos. En la VM:

```bash
docker compose exec mysql-monolith sh -c 'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" iobuild -e "SELECT Id,Name,Type FROM devices WHERE UnitId IS NOT NULL;"'
```

### 4.6 Decirle al simulador que no toque esos IDs

En el `.env` de la VM: `HARDWARE_DEVICE_IDS=<idClima>,<idLuz>,<idNodo>` y luego:

```bash
docker compose -f docker-compose.yml -f docker-compose.vm.yml --profile telemetry up -d simulator iot-monitor
```

### 4.7 Probar el broker desde fuera (desde tu PC)

```bash
docker run --rm eclipse-mosquitto:2 mosquitto_sub -h IP_PUBLICA -p 1884 -u iobuild -P 'TU_MQTT_PASSWORD' -t 'telemetry/#' -v -C 3
```

Si muestra mensajes o se queda esperando sin error, el puerto está abierto. `Connection refused`
o timeout = puerto 1884 cerrado en el firewall del proveedor.

---

## 5. Apuntar el ESP32 a la VM

Edita `hardware/firmware/iobuild_edge_node/secrets.h`:

```c
#define WIFI_SSID "nombre-de-tu-red-2.4GHz"   // nombre, NO la MAC del router
#define WIFI_PASS "clave-wifi"
#define MQTT_HOST "IP_PUBLICA_DE_LA_VM"
#define MQTT_PORT 1884
#define MQTT_USER "iobuild"
#define MQTT_PASS "el MQTT_PASSWORD del .env de la VM"
#define ID_LUZ   <idLuz>
#define ID_CLIMA <idClima>
#define ID_NODO  <idNodo>
```

Flashear (Arduino IDE: Subir) o por terminal:

```bash
ARDUINO_CLI="/Applications/Arduino IDE.app/Contents/Resources/app/lib/backend/resources/arduino-cli"
"$ARDUINO_CLI" compile --fqbn esp32:esp32:esp32 hardware/firmware/iobuild_edge_node
"$ARDUINO_CLI" upload -p /dev/cu.usbserial-0001 --fqbn esp32:esp32:esp32 hardware/firmware/iobuild_edge_node
```

Ahora el ESP32 funciona con **cualquier WiFi 2.4 GHz** (casa, hotspot del celular)
porque la VM es pública. Evita redes con portal cautivo (universidad, hotel):
usa el hotspot del celular en la presentación.

---

## 6. Checklist de verificación

- [ ] Monitor serie: `PLATAFORMA: CONECTADO` y `nube=OK`.
- [ ] LED verde fijo.
- [ ] Front → Dashboard (Owner) → Select Device → Nodo: humedad y temperatura iguales al monitor serie.
- [ ] Gestión de dispositivos → Smart Light → power Off → Send: monitor serie muestra
      `orden commands/<idLuz>: {"power":false}` y la luz ya no prende con el PIR.
- [ ] Power On otra vez → la luz vuelve a funcionar con el PIR.
- [ ] Nodo → `alarmEnabled` Off → el buzzer no suena aunque suba la temperatura.

---

## 7. Problemas comunes

| Síntoma (monitor serie) | Causa | Solución |
|---|---|---|
| `[WiFi] conectando a ...` sin parar | SSID/clave mal, red 5 GHz o portal cautivo | Nombre exacto de red 2.4 GHz; prueba hotspot del celular |
| `[MQTT] sin conexión (estado -2)` | No llega al broker | IP/host mal, puerto 1884 cerrado, o cambió la IP de tu PC |
| `estado 4` o `5` | Usuario/clave MQTT incorrectos | `MQTT_USER`/`MQTT_PASS` = valores del `.env` de la VM |
| Conecta pero el dashboard no cambia | IDs de `secrets.h` ≠ IDs de la app | Revisa los IDs (paso 4.5) |
| Valores que saltan solos | Falta `HARDWARE_DEVICE_IDS` | Paso 4.6 |
| `secrets.h: No such file` | No copiaste el ejemplo | `cp secrets.example.h secrets.h` |
| Relés al revés | Tipo de módulo | Cambia `RELE_ACTIVO_BAJO` |
| Hora 1970 en logs | Sin NTP aún | Normal los primeros segundos; el backend usa su hora |

En la VM, logs del backend: `docker compose logs -f iobuild-api | grep -i mqtt`.
Un JSON inválido aparece como `telemetry rejected`.

---

## 8. Seguridad (para producción, no bloquea la demo)

- MQTT en 1884 va **sin cifrar**: para producción usar TLS en 8883 y `WiFiClientSecure` en el ESP32.
- No abras 8090 (monitor) ni 3306 (MySQL) a internet. Para ver el monitor: `ssh -L 8090:localhost:8090 usuario@IP_PUBLICA` y abre http://localhost:8090.
- Cambia `JWT_SECRET` y contraseñas de la base antes de usar datos reales.
- Front en Hostinger (HTTPS) llamando a la VM por HTTP será bloqueado por el navegador
  (contenido mixto). Para la demo usa el front de la VM (`http://IP_PUBLICA`), o pon un
  dominio con HTTPS a la VM.
