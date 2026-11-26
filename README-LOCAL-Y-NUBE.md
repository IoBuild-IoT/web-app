# IoBuild: cómo funciona en local y plan para llevar el front a la nube

Guía práctica para levantar todo en tu PC con el ESP32 real, entender cómo fluyen los
datos y desplegar el frontend en la nube.

---

## 1. Arquitectura

```
                   WiFi 2.4 GHz                       red Docker (iobuild-network)
 ┌──────────┐  MQTT :1884   ┌───────────┐   ┌──────────────┐   ┌──────────┐
 │  ESP32   │ ────────────> │ Mosquitto │ ─>│ API .NET 9   │ ─>│  MySQL   │  estado, órdenes, historial
 │ (nodo)   │ <──────────── │  broker   │   │ iobuild-api  │ ─>│ InfluxDB │  telemetría en vivo
 └──────────┘ commands/{id} └───────────┘   └──────┬───────┘   └──────────┘
                                                   │ /api/v1
 ┌──────────┐   http://localhost   ┌───────┐       │
 │Navegador │ ───────────────────> │ nginx │ ──────┘  /api  -> API
 └──────────┘                      │ :80   │ ───────────── /     -> frontend (Vue)
                                   └───────┘
```

- **El ESP32 no habla con el front.** Publica en el broker MQTT; el backend guarda y el
  front consulta la API.
- **Tópicos:** `telemetry/{id}` (placa → plataforma) y `commands/{id}` (plataforma → placa,
  QoS 1, retenido). La placa confirma una orden repitiéndola en `reported`.
- **Simulador:** genera datos falsos para los dispositivos que no son hardware real.
  `HARDWARE_DEVICE_IDS` le dice cuáles debe ignorar.

### Servicios y puertos (local)

| Servicio | Puerto en tu PC | Para qué |
|---|---|---|
| nginx | `80` | Entrada única: front en `/`, API en `/api` |
| iobuild-api | `8080` | API directa y Swagger (`/swagger`) |
| frontend | interno | Vue compilado servido por su propio nginx |
| mosquitto | `1884` | Broker MQTT, aquí se conecta el ESP32 (dentro de Docker usa 1883) |
| mysql-monolith | interno | Base de datos principal |
| influxdb | interno | Telemetría en vivo (perfil `telemetry`) |
| simulator | interno | Dispositivos simulados |
| iot-monitor | `8090` | Visor del tráfico MQTT (sin login, solo local) |
| phpmyadmin | `127.0.0.1:8082` | Opcional, perfil `db-admin` |

---

## 2. Requisitos

- Docker Desktop.
- Arduino IDE con el core **esp32 by Espressif** y las librerías *DHT sensor library*,
  *Adafruit Unified Sensor*, *PubSubClient* y *ArduinoJson 7.x*.
- La PC y el ESP32 en **la misma red WiFi de 2.4 GHz**. Con hotspot de iPhone, activa
  **Maximizar compatibilidad** y deja abierta la pantalla de Compartir Internet mientras
  la placa se conecta.

---

## 3. Levantar todo en local

> Si Docker falla al montar volúmenes, mueve o clona `web-app` a una ruta sin `:`
> (por ejemplo `~/iobuild/web-app`). La carpeta de Google Drive contiene `P:` en la ruta.

1. **Variables:** copia `.env.example` a `.env` y completa los valores. Revisa que no haya
   claves repetidas (Docker usa la última).
2. **Levantar:**

   ```bash
   docker compose --profile telemetry up -d --build --wait
   ```

3. **Comprobar:** abre http://localhost. La API responde en http://localhost/api/v1/devices/types.
4. **Cuentas y dispositivos** (solo la primera vez; los datos quedan en volúmenes Docker):
   1. Registra un **Builder**, elige un plan (pagos simulados si `STRIPE_USE_SIMULATED_PAYMENTS=true`)
      y crea un proyecto con 1 unidad. La unidad trae *Smart Light* y *Air Conditioner*.
   2. En **Clientes**, agrega al Owner con su email y asígnale la unidad.
   3. Registra al **Owner** con ese email y, en **Gestión de dispositivos**, agrega
      **Edge Node (ESP32)**.
   4. Anota los IDs de los tres dispositivos y ponlos en `.env`:
      `HARDWARE_DEVICE_IDS=<clima>,<luz>,<nodo>`. Luego reinicia el simulador:

      ```bash
      docker compose --profile telemetry up -d simulator iot-monitor
      ```

Apagar sin perder datos:

```bash
docker compose --profile telemetry down
```

---

## 4. Conectar el ESP32

1. Copia `hardware/firmware/iobuild_edge_node/secrets.example.h` a `secrets.h`
   (está en `.gitignore`) y completa:
   - `WIFI_SSID`: el **nombre** de la red (no la MAC del router) y `WIFI_PASS`.
   - `MQTT_HOST`: la IP de tu PC en esa red. En Mac:

     ```bash
     ipconfig getifaddr en0
     ```

   - `MQTT_USER` / `MQTT_PASS`: vacíos en local.
   - `ID_LUZ`, `ID_CLIMA`, `ID_NODO`: los IDs del paso 3.4.
2. Compila y sube (Arduino IDE → Subir, placa *ESP32 Dev Module*). Sin Arduino IDE:
   compila con Docker y flashea el `.bin` (ver [hardware/README.md](hardware/README.md#compilar-el-firmware-con-docker-sin-arduino-ide)):

   ```bash
   docker compose -f hardware/firmware/docker-compose.yml run --rm firmware
   ```
3. Monitor serie a 115200. Debe aparecer:

   ```
   [MQTT] conectado a 192.168.x.x:1884 (IP nodo ...)
   PLATAFORMA: CONECTADO
   ... nube=OK
   ```

**Cada vez que cambies de red** revisa la IP de la PC. Si cambió, actualiza `MQTT_HOST`
y vuelve a subir el firmware.

### Pines del prototipo

| Componente | Pin | Notas |
|---|---|---|
| DHT22 | D4 | Temperatura y humedad |
| PIR | D27 | `INPUT_PULLDOWN`, alimentado a 5 V |
| Relé 1 + LED amarillo | D26 | Luz del pasillo, solo encender/apagar |
| Relé 2 + LED azul | D25 | Clima, solo encender/apagar |
| LED verde | D18 | Estado del nodo (directo al pin) |
| Buzzer | D23 | Alarma |
| Servo SG90 | D19 | Persiana, 0° cerrada a 180° abierta |

---

## 5. Cómo se actualizan los datos

| Paso | Frecuencia |
|---|---|
| ESP32 publica telemetría | cada **3 s** y **al instante** si cambia luz, clima, alarma o presencia |
| Front consulta la API | cada **3 s**, **solo mientras el ESP32 está conectado** |
| "Conectado" | la API marca `connected: true` si la última lectura tiene ≤ 10 s |

- **Indicador:** una barra arriba del Dashboard y del Panel de control muestra
  *ESP32 conectado* (verde), *Buscando* (ámbar) o *ESP32 desconectado* (rojo).
- **Desconectado:** el front deja de consultar y no reintenta solo. El botón
  **Conectar con ESP32** comprueba 4 veces (cada 2 s) si la placa volvió a reportar.
  El ESP32 es quien se conecta al broker; tras encenderlo tarda unos 30 s.
- **Retraso esperado:** cambios de estado en ~3 s. Temperatura, humedad y posición del
  servo hasta ~6 s. El servo tarda ~7 s en recorrer 180°.
- **Sin internet**, la placa sigue funcionando con sus reglas locales y los últimos ajustes
  guardados en su memoria.

---

## 6. Qué puede hacer cada pantalla (Owner)

- **Dashboard** (`/analytics/dashboard`): estado en vivo (en línea, temperatura, humedad,
  presencia, luz, clima, persiana, alarma), energía, gráficas y alertas reales.
- **Gestión de dispositivos** (`/devices/device-management`): una tarjeta por componente.
  - Luz del pasillo y Clima: encendido/apagado; el clima elige Frío/Calor/Ventilador.
    El brillo y la temperatura objetivo se ocultan porque los relés no los soportan.
  - Sensor de movimiento: activar/desactivar y tiempo encendida.
  - Persiana (servo): Automática o Manual (Cerrar, Mitad, Abrir o ángulo exacto).
  - Alarma: activar/desactivar y umbral.

---

## 7. Pruebas

```bash
cd frontend && npm ci && npx vitest run && npm run build
```

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 dotnet test backend/IoBuild.sln
```

E2E con Docker (usan los puertos 80/8080/1884: detén antes el stack local):

```bash
bash backend/tests/Integration/run_esp32_node_e2e.sh
```

```bash
bash backend/tests/Integration/run_edge_node_settings_e2e.sh
```

---

## 8. Problemas comunes

| Síntoma | Causa | Solución |
|---|---|---|
| Serie: `[WiFi] conectando ... (estado 1)` | Red no encontrada o en 5 GHz | Red de 2.4 GHz; en iPhone, Maximizar compatibilidad |
| Serie: `(estado 5)` o `(estado 6)` | El hotspot rechaza la conexión | Deja abierta la pantalla de Compartir Internet y reinicia la placa |
| Serie: `[MQTT] sin conexión (estado -2)` | IP de la PC cambió o puerto cerrado | Actualiza `MQTT_HOST` y reflashea |
| Front: *ESP32 desconectado* con la placa encendida | La placa no llega al broker | Revisa el monitor serie y pulsa **Conectar con ESP32** |
| Valores que saltan solos | Falta `HARDWARE_DEVICE_IDS` | Paso 3.4 |
| Relés al revés | Tipo de módulo | Cambia `RELE_ACTIVO_BAJO` en `secrets.h` |
| Login devuelve error vacío tras reiniciar Docker | La API aún arranca | Espera unos segundos; reintenta |

---

## 9. Plan de implementación del frontend en la nube

### 9.1 Situación actual

- El front es una SPA de Vue compilada con Vite. La URL de la API se fija **al compilar**
  con `VITE_API_URL` (hoy `/api/v1`, relativa).
- Con la URL relativa, el front y la API deben estar **en el mismo dominio** (nginx hace
  de puerta única). Por eso funciona en local sin CORS.
- El ESP32 nunca depende del front: necesita el **broker MQTT** accesible, es decir, el
  backend en un servidor con IP o dominio público.

### 9.2 Opciones

| Opción | Cómo | Ventajas | Cuidado |
|---|---|---|---|
| **A. Front junto al backend (recomendada)** | Mismo VPS/VM con Dokploy o `docker-compose.vm.yml`; un dominio con HTTPS; `/` al front y `/api` a la API | Sin CORS, sin contenido mixto, un solo despliegue, `VITE_API_URL=/api/v1` no cambia | El VPS sirve también los archivos estáticos |
| **B. Front en hosting estático** (Vercel, Netlify, Hostinger) | Compilar con `VITE_API_URL=https://api.tu-dominio/api/v1` | CDN, despliegue por push | La API necesita **HTTPS** propio (si no, el navegador bloquea las llamadas) y CORS con el dominio del front |

**Recomendación:** opción A para la demo y la entrega. Pasar a B solo si se necesita CDN.

### 9.3 Fases (opción A)

**Fase 0. Preparación (≈1 h)**
1. Contratar VPS/VM Ubuntu 22.04/24.04, **2 vCPU y 4 GB RAM** mínimo.
2. Dominio o subdominio (ej. `app.tu-dominio.com`) apuntando a la IP pública (registro A).
3. Firewall del proveedor: abrir `22`, `80`, `443` y `1884/TCP`. **No** abrir `8090` ni `3306`.

**Fase 1. Servidor (≈1 h)**
1. Instalar Docker (o Dokploy) en el VPS.
2. Clonar el repo y copiar `.env` por `scp` (no está en git).
3. En `.env`: `MQTT_PASSWORD` fuerte, `JWT_SECRET` nuevo, contraseñas de BD nuevas,
   `CORS_ALLOWED_ORIGINS=https://app.tu-dominio.com`, `HARDWARE_DEVICE_IDS` vacío por ahora.

**Fase 2. Despliegue (≈1 h)**
- Dokploy: Compose Path `./docker-compose.dokploy.yml`. Dominios: `frontend:80` en `/` y
  `iobuild-api:8080` en `/api`, `/swagger`, `/health` (sin *Strip Path*). Dokploy emite
  el certificado HTTPS.
- Sin Dokploy:

  ```bash
  docker compose -f docker-compose.yml -f docker-compose.vm.yml --profile telemetry up -d --build
  ```

  y poner HTTPS delante (Caddy o Traefik) apuntando a `nginx:80`.

**Fase 3. Datos y hardware (≈30 min)**
1. Crear Builder, proyecto, cliente, Owner y Edge Node en `https://app.tu-dominio.com`.
2. Poner los IDs nuevos en `HARDWARE_DEVICE_IDS` y reiniciar `simulator`.
3. En `secrets.h`: `MQTT_HOST` = dominio o IP pública, `MQTT_USER=iobuild`,
   `MQTT_PASS` = `MQTT_PASSWORD`, IDs nuevos. Reflashear.

**Fase 4. Verificación (≈30 min)**
- [ ] `https://app.tu-dominio.com` carga y el login funciona.
- [ ] `https://app.tu-dominio.com/health` responde 200.
- [ ] Monitor serie: `PLATAFORMA: CONECTADO` desde una red distinta a la del VPS.
- [ ] Barra *ESP32 conectado* y datos en vivo en el Dashboard.
- [ ] Una orden desde el front (persiana Abrir) mueve el servo.
- [ ] Desenchufar la placa: en ≤10 s aparece *ESP32 desconectado* y se detiene el polling.

**Fase 5. Operación**
- Copia de seguridad de los volúmenes `mysql-monolith` e `influxdb`.
- Actualizar: `git pull` y volver a desplegar (o redeploy en Dokploy).
- Revertir: desplegar el commit anterior; los volúmenes conservan los datos.

### 9.4 Si se elige la opción B (front separado)

1. La API debe tener su propio dominio con HTTPS, por ejemplo `api.tu-dominio.com`.
2. Compilar el front:

   ```bash
   cd frontend && VITE_API_URL=https://api.tu-dominio.com/api/v1 npm run build
   ```

   (en Vercel/Netlify, definir `VITE_API_URL` y las demás `VITE_*` en las variables del proyecto).
3. Rutas de la SPA: Vercel ya usa `frontend/vercel.json`; en Hostinger/Apache hace falta un
   `.htaccess` que redirija todo a `index.html`.
4. En el backend: `CORS_ALLOWED_ORIGINS=https://<dominio-del-front>`.
5. El broker MQTT sigue en el VPS: nada cambia para el ESP32.

### 9.5 Riesgos

| Riesgo | Mitigación |
|---|---|
| MQTT en 1884 viaja sin cifrar | Aceptable para la demo con usuario/clave; producción: TLS en 8883 y `WiFiClientSecure` en el ESP32 |
| Front HTTPS llamando a API HTTP | Bloqueado por el navegador: usar opción A o dar HTTPS a la API |
| `iot-monitor` sin login | No exponerlo; usar túnel SSH (`ssh -L 8090:localhost:8090 usuario@IP`) |
| Redes con portal cautivo (universidad) | El ESP32 no puede pasarlas: usar hotspot del celular |
| Secretos en el repo | `.env` y `secrets.h` están en `.gitignore`; rotar claves si alguna se filtra |

Más detalle del servidor y del ESP32: [hardware/GUIA-VM-Y-ESP32.md](hardware/GUIA-VM-Y-ESP32.md).
