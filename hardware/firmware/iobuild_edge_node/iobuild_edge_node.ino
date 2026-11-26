// Arduino IDE: Placa "ESP32 Dev Module"
// Librerías: "DHT sensor library" (Adafruit) + "Adafruit Unified Sensor" + "PubSubClient" + "ArduinoJson" (7.x)
/* IoBuild · Nodo conectado (mismas reglas que el modo local + WiFi + MQTT con la plataforma)
 * Pines: DHT22=4 · PIR=27 · Relé1 (luz pasillo)=26 · Relé2 (clima)=25 · LED estado=18 · Buzzer=23 · Servo=19
 * Reglas locales (siguen funcionando sin internet):
 *   R1 Luz pasillo : PIR -> relé 1 ON; OFF lightHoldSeconds (10 s) tras el último movimiento
 *   R2 Clima       : cooling: T >= amb+climateDelta (1.5) o HR >= amb+10 % -> ON; OFF bajo amb+(delta-1) y amb+5 %
 *                    heating: T <= amb-climateDelta -> ON · fan: solo humedad
 *   R3 Persiana    : automática: T sube blindStep (1 °C) -> ABRE (180°); baja blindStep -> CIERRA (0°), barrido lento
 *                    manual (blindAuto=false): el servo va al ángulo blindAngle (0-180°) pedido desde la app
 *   Sensor PIR     : motionEnabled=false lo desactiva (la luz del pasillo no se enciende por movimiento)
 *   R4 Alarma      : T >= amb+alarmDelta (3) -> buzzer + LED rápido; se libera bajo amb+(alarmDelta-1)
 *   LED estado     : fijo = OK y conectado · lento = sin plataforma o DHT22 sin lectura · rápido = alarma
 * Plataforma (contrato MQTT de IoBuild, igual que iot-simulator):
 *   commands/{id}  <- estado deseado completo (QoS 1, retenido): luz {power,brightness},
 *                     clima {power,mode,targetTemperature},
 *                     nodo {lightHoldSeconds,climateDelta,alarmDelta,blindStep,blindAngle,alarmEnabled,motionEnabled,blindAuto}
 *   telemetry/{id} -> cada 5 s y tras cada orden; `reported` repite el deseado para confirmarlo
 * La configuración (WiFi, broker, IDs) va en secrets.h (copiar de secrets.example.h).
 */
#include <Arduino.h>
#include <DHT.h>
#include <WiFi.h>
#include <PubSubClient.h>
#include <ArduinoJson.h>
#include <Preferences.h>
#include <sys/time.h>
#include <time.h>
#include "secrets.h"

#define PIN_DHT     4
#define PIN_PIR     27
#define PIN_RELE1   26
#define PIN_RELE2   25
#define PIN_ESTADO  18
#define PIN_BUZZER  23
#define PIN_SERVO   19

const uint32_t PIR_CALENTAMIENTO_MS = 30000;
const uint32_t T_MUESTREO   = 2000;
const uint32_t T_TELEMETRIA = 3000;   // latido; además se publica al instante cuando algo cambia
const uint32_t T_RECONEXION = 5000;

// Persiana: recorrido y velocidad del servo (más ms = más lento)
const int CERRADA = 0, ABIERTA = 180;
const uint32_t MS_POR_GRADO = 1;   // 1 ms/grado ≈ 0.2 s de giro; con 20 son ~3.6 s
const float CLIMA_ON_H = 10, CLIMA_OFF_H = 5;   // % HR sobre ambiente

// Umbrales configurables desde el front (dispositivo Edge Node). Valores por defecto = modo local.
uint32_t HOLD_MS      = 10000;
float    CLIMA_ON_T   = 1.5;
float    ALARMA_ON_T  = 3.0;
float    PASO_SERVO   = 1.0;
bool     ALARMA_ACTIVA = true;
bool     PIR_ACTIVO    = true;    // motionEnabled
bool     PERSIANA_AUTO = true;    // blindAuto
int      ANGULO_MANUAL = 0;       // blindAngle (solo con PERSIANA_AUTO = false)
float climaOffT()  { return max(0.2f, CLIMA_ON_T - 1.0f); }
float alarmaOffT() { return ALARMA_ON_T - 1.0f; }

// Órdenes de la app para luz y clima
bool  luzHabilitada = true;  float brillo = 100;
bool  climaPower = true;     String climaModo = "cooling";  float climaTarget = 24;

DHT dht(PIN_DHT, DHT22);
WiFiClient wifiCliente;
PubSubClient mqtt(wifiCliente);
Preferences prefs;

// Último estado deseado recibido por dispositivo; se repite tal cual en `reported`
JsonDocument desLuz, desClima, desNodo;
volatile bool pendLuz = false, pendClima = false, pendNodo = false;

// Estado
bool luzOn = false, climaOn = false, alarma = false, dhtFalla = false;
int  persiana = CERRADA, servoPos = CERRADA;
uint32_t tServo = 0, tUltMov = 0, tMuestreo = 0, tTelemetria = 0, tWifi = 0, tMqtt = 0;
bool hayMov = false, pirEst = false;
float tRef = NAN, tAmb = NAN, hAmb = NAN, tAct = NAN, hAct = NAN;
int  fallasDht = 0;
bool pirPrev = false, luzPrev = false, climaPrev = false, alarmaPrev = false, plataformaPrev = false;
int  persianaPrev = CERRADA;
uint32_t bootTag = 0, contador = 0;
uint32_t tPub[3] = {0, 0, 0};

// ---------- Actuadores ----------
#if defined(ESP_ARDUINO_VERSION_MAJOR) && ESP_ARDUINO_VERSION_MAJOR >= 3
void servoInit()           { ledcAttach(PIN_SERVO, 50, 16); }
void servoDuty(uint32_t d) { ledcWrite(PIN_SERVO, d); }
#else
void servoInit()           { ledcSetup(4, 50, 16); ledcAttachPin(PIN_SERVO, 4); }  // canal 4: no choca con tone()
void servoDuty(uint32_t d) { ledcWrite(4, d); }
#endif

void moverServo(int grados) {
  uint32_t us = map(constrain(grados, 0, 180), 0, 180, 500, 2400);
  servoDuty(us * 65535UL / 20000UL);
}

void rele(int pin, bool on) { digitalWrite(pin, (on != RELE_ACTIVO_BAJO) ? HIGH : LOW); }
void buzzer(bool on) { if (on) tone(PIN_BUZZER, 2000); else noTone(PIN_BUZZER); }

void probarPin(int pin, const char *nombre) {
  Serial.printf("\n[%s]\n", nombre);
  Serial.println("  Señal ALTA (3.3 V) 2 s -> debe PRENDER si el LED va directo o el relé es activo-alto");
  digitalWrite(pin, HIGH); delay(2000);
  Serial.println("  Señal BAJA (0 V) 2 s   -> si prende AHORA y no antes: relé activo-bajo, RELE_ACTIVO_BAJO = true");
  digitalWrite(pin, LOW);  delay(2000);
  Serial.println("  Si no prendió en ninguno de los dos: revisa cableado, polaridad del LED o alimentación del relé");
  rele(pin, false);
}

void autotest() {
  Serial.println("\n======== VALIDACIÓN DE CONEXIONES ========");
  probarPin(PIN_RELE1, "1/5 LED amarillo · luz pasillo · D26");
  probarPin(PIN_RELE2, "2/5 LED azul · clima · D25");
  Serial.println("\n[3/5 LED verde · estado · D18]  ENCENDIDO 2 s");
  digitalWrite(PIN_ESTADO, HIGH); delay(2000); digitalWrite(PIN_ESTADO, LOW);
  Serial.println("\n[4/5 Buzzer · alarma · D23]  3 pitidos");
  for (int i = 0; i < 3; i++) { buzzer(true); delay(300); buzzer(false); delay(300); }
  Serial.println("\n[5/5 Servo · persiana · D19]  0° -> 180° -> 0°");
  for (int g = CERRADA; g <= ABIERTA; g++) { moverServo(g); delay(MS_POR_GRADO); }
  delay(500);
  for (int g = ABIERTA; g >= CERRADA; g--) { moverServo(g); delay(MS_POR_GRADO); }
  Serial.println("\n======== FIN VALIDACIÓN ========\n");
}

// ---------- Órdenes de la plataforma ----------
void aplicarLuz(JsonObjectConst d) {
  if (d["power"].is<bool>())       luzHabilitada = d["power"];
  if (d["brightness"].is<float>()) brillo = d["brightness"];
}
void aplicarClima(JsonObjectConst d) {
  if (d["power"].is<bool>())              climaPower = d["power"];
  if (d["mode"].is<const char *>())       climaModo = d["mode"].as<const char *>();
  if (d["targetTemperature"].is<float>()) climaTarget = d["targetTemperature"];
}
void aplicarNodo(JsonObjectConst d) {
  if (d["lightHoldSeconds"].is<float>()) HOLD_MS = (uint32_t)(d["lightHoldSeconds"].as<float>() * 1000);
  if (d["climateDelta"].is<float>())     CLIMA_ON_T = d["climateDelta"];
  if (d["alarmDelta"].is<float>())       ALARMA_ON_T = d["alarmDelta"];
  if (d["blindStep"].is<float>())        PASO_SERVO = d["blindStep"];
  if (d["alarmEnabled"].is<bool>())      ALARMA_ACTIVA = d["alarmEnabled"];
  if (d["motionEnabled"].is<bool>())     PIR_ACTIVO = d["motionEnabled"];
  if (d["blindAuto"].is<bool>())         PERSIANA_AUTO = d["blindAuto"];
  if (d["blindAngle"].is<float>())       ANGULO_MANUAL = constrain((int)d["blindAngle"].as<float>(), CERRADA, ABIERTA);
}

void guardar(const char *clave, JsonDocument &doc) {
  String json; serializeJson(doc, json);
  prefs.begin("iobuild", false); prefs.putString(clave, json); prefs.end();
}

// Sin internet al arrancar, el nodo sigue con las últimas órdenes guardadas en NVS
void cargarGuardado() {
  prefs.begin("iobuild", true);
  String luz = prefs.getString("luz", ""), clima = prefs.getString("clima", ""), nodo = prefs.getString("nodo", "");
  prefs.end();
  if (luz.length()   && !deserializeJson(desLuz, luz))     aplicarLuz(desLuz.as<JsonObjectConst>());
  if (clima.length() && !deserializeJson(desClima, clima)) aplicarClima(desClima.as<JsonObjectConst>());
  if (nodo.length()  && !deserializeJson(desNodo, nodo))   aplicarNodo(desNodo.as<JsonObjectConst>());
  if (desLuz.isNull())   { desLuz["power"] = true; desLuz["brightness"] = 100; }
  if (desClima.isNull()) { desClima["power"] = true; desClima["mode"] = "cooling"; desClima["targetTemperature"] = 24; }
  if (desNodo.isNull())  { desNodo["lightHoldSeconds"] = 10; desNodo["climateDelta"] = 1.5; desNodo["alarmDelta"] = 3; desNodo["blindStep"] = 1; desNodo["blindAngle"] = 0; desNodo["alarmEnabled"] = true; desNodo["motionEnabled"] = true; desNodo["blindAuto"] = true; }
}

void alRecibir(char *topic, byte *payload, unsigned int len) {
  int id = atoi(topic + strlen("commands/"));
  JsonDocument doc;
  // const: ArduinoJson copia los textos; el búfer de PubSubClient se reutiliza al publicar
  if (deserializeJson(doc, (const byte *)payload, len) || !doc.is<JsonObject>()) {
    Serial.printf("[MQTT] orden ignorada en %s (JSON inválido)\n", topic);
    return;
  }
  JsonObjectConst d = doc.as<JsonObjectConst>();
  String txt; serializeJson(doc, txt);
  Serial.printf("[MQTT] orden %s: %s\n", topic, txt.c_str());
  if (id == ID_LUZ && ID_LUZ)          { aplicarLuz(d);   desLuz.set(d);   guardar("luz", desLuz);     pendLuz = true; }
  else if (id == ID_CLIMA && ID_CLIMA) { aplicarClima(d); desClima.set(d); guardar("clima", desClima); pendClima = true; }
  else if (id == ID_NODO && ID_NODO)   { aplicarNodo(d);  desNodo.set(d);  guardar("nodo", desNodo);   pendNodo = true; }
}

// ---------- Telemetría ----------
bool horaValida() { return time(nullptr) > 1700000000; }

void marcaTiempo(char *buf, size_t n) {
  if (!horaValida()) { snprintf(buf, n, "1970-01-01T00:00:00.000Z"); return; }  // el backend usa su hora
  struct timeval tv; gettimeofday(&tv, nullptr);
  struct tm t; gmtime_r(&tv.tv_sec, &t);
  size_t k = strftime(buf, n, "%Y-%m-%dT%H:%M:%S", &t);
  snprintf(buf + k, n - k, ".%03ldZ", (long)(tv.tv_usec / 1000));
}

void publicar(int id, int idx, JsonDocument &reported, bool encendido, float watts) {
  if (!id || !mqtt.connected()) return;
  uint32_t now = millis();
  float horas = tPub[idx] ? (now - tPub[idx]) / 3600000.0f : T_TELEMETRIA / 3600000.0f;
  tPub[idx] = now;
  char ts[32]; marcaTiempo(ts, sizeof ts);
  char evento[48]; snprintf(evento, sizeof evento, "esp32-%d-%08lx-%lu", id, (unsigned long)bootTag, (unsigned long)++contador);

  JsonDocument doc;
  doc["deviceId"] = id;
  doc["eventId"] = evento;
  doc["timestamp"] = ts;
  doc["status"] = encendido ? "online" : "idle";
  doc["reported"] = reported;
  doc["energy_kwh"] = watts * horas / 1000.0f;   // estimado: luz 10 W, clima 1200 W, nodo 0.5 W
  doc["temperature_c"] = isnan(tAct) ? 0 : tAct;
  doc["voltage_v"] = 220;
  JsonObject node = doc["node"].to<JsonObject>();
  if (!isnan(hAct)) node["humidity"] = hAct;
  node["presence"] = pirEst && PIR_ACTIVO;
  node["blind_deg"] = servoPos;
  node["alarm"] = alarma;
  node["light"] = luzOn;      // estado real de los relés (no la orden)
  node["climate"] = climaOn;

  char buf[640]; size_t n = serializeJson(doc, buf, sizeof buf);
  char topic[32]; snprintf(topic, sizeof topic, "telemetry/%d", id);
  if (!mqtt.publish(topic, (const uint8_t *)buf, n)) Serial.printf("[MQTT] no se pudo publicar %s\n", topic);
}

void publicarTodo() {
  publicar(ID_LUZ, 0, desLuz, luzHabilitada, luzOn ? 10 : 0.5);
  publicar(ID_CLIMA, 1, desClima, climaPower, climaOn ? 1200 : 0.5);
  publicar(ID_NODO, 2, desNodo, true, 0.5);
}

// ---------- Conexión ----------
void suscribir(int id) {
  if (!id) return;
  char topic[32]; snprintf(topic, sizeof topic, "commands/%d", id);
  mqtt.subscribe(topic, 1);   // las órdenes retenidas llegan al suscribirse
}

void mantenerConexion(uint32_t now) {
  if (WiFi.status() != WL_CONNECTED) {
    // Un hotspot de celular puede tardar en aceptar: se reintenta cada 20 s sin cortar el intento en curso.
    // Estado: 1 = red no encontrada (apagada o en 5 GHz), 4 = falló la conexión (clave), 6 = desconectado.
    if (now - tWifi >= 20000) { tWifi = now; Serial.printf("[WiFi] conectando a %s (estado %d)...\n", WIFI_SSID, (int)WiFi.status()); WiFi.begin(WIFI_SSID, WIFI_PASS); }
    return;
  }
  if (!mqtt.connected() && now - tMqtt >= T_RECONEXION) {
    tMqtt = now;
    char cliente[32]; snprintf(cliente, sizeof cliente, "iobuild-esp32-%08lx", (unsigned long)bootTag);
    bool ok = strlen(MQTT_USER) ? mqtt.connect(cliente, MQTT_USER, MQTT_PASS) : mqtt.connect(cliente);
    if (ok) {
      Serial.printf("[MQTT] conectado a %s:%d (IP nodo %s)\n", MQTT_HOST, MQTT_PORT, WiFi.localIP().toString().c_str());
      suscribir(ID_LUZ); suscribir(ID_CLIMA); suscribir(ID_NODO);
      tTelemetria = 0;   // telemetría inmediata
    } else {
      Serial.printf("[MQTT] sin conexión (estado %d) -> %s:%d\n", mqtt.state(), MQTT_HOST, MQTT_PORT);
    }
  }
  mqtt.loop();
}

// ---------- Setup ----------
void setup() {
  Serial.begin(115200);
  delay(300);
  bootTag = esp_random();
  pinMode(PIN_PIR, INPUT_PULLDOWN);
  pinMode(PIN_RELE1, OUTPUT);  rele(PIN_RELE1, false);
  pinMode(PIN_RELE2, OUTPUT);  rele(PIN_RELE2, false);
  pinMode(PIN_ESTADO, OUTPUT); digitalWrite(PIN_ESTADO, LOW);
  pinMode(PIN_BUZZER, OUTPUT);
  dht.begin();
  servoInit();
  Serial.println("IoBuild · nodo conectado");
  Serial.printf("IDs: luz=%d clima=%d nodo=%d · broker %s:%d\n", ID_LUZ, ID_CLIMA, ID_NODO, MQTT_HOST, MQTT_PORT);
  cargarGuardado();
  autotest();
  moverServo(servoPos);

  WiFi.mode(WIFI_STA);
  WiFi.setAutoReconnect(true);
  WiFi.begin(WIFI_SSID, WIFI_PASS);
  tWifi = millis();
  configTime(0, 0, "pool.ntp.org", "time.google.com");   // hora UTC para los timestamps
  mqtt.setServer(MQTT_HOST, MQTT_PORT);
  mqtt.setCallback(alRecibir);
  mqtt.setBufferSize(1024);
  mqtt.setSocketTimeout(3);
  Serial.println("PIR calentando 30 s (se ignora). No toques el DHT22: se toma el ambiente de referencia.");
}

// ---------- Loop ----------
void loop() {
  uint32_t now = millis();
  mantenerConexion(now);
  bool plataforma = mqtt.connected();
  if (plataforma != plataformaPrev) { plataformaPrev = plataforma; Serial.printf("[%6lus] PLATAFORMA: %s\n", now / 1000, plataforma ? "CONECTADO" : "SIN CONEXIÓN (reglas locales activas)"); }

  // R1: PIR con calentamiento y antirrebote de 300 ms
  static bool pirRaw = false; static uint32_t tRaw = 0;
  bool lect = digitalRead(PIN_PIR);
  if (lect != pirRaw) { pirRaw = lect; tRaw = now; }
  if (now - tRaw >= 300) pirEst = lect;
  bool pir = pirEst && PIR_ACTIVO;
  if (!PIR_ACTIVO) hayMov = false;
  if (now > PIR_CALENTAMIENTO_MS && pir && (!pirPrev || !hayMov)) { tUltMov = now; hayMov = true; }
  // La luz va por relé (solo enciende/apaga): el brillo recibido de la app no la condiciona.
  luzOn = luzHabilitada && hayMov && (now - tUltMov < HOLD_MS);
  rele(PIN_RELE1, luzOn);

  if (pir != pirPrev) {
    pirPrev = pir;
    Serial.printf("[%6lus] PIR: %s%s\n", now / 1000, pir ? "MOVIMIENTO detectado" : "sin movimiento",
                  (pir && now <= PIR_CALENTAMIENTO_MS) ? "  (ignorado: calentando)" : "");
  }
  if (luzOn != luzPrev) {
    luzPrev = luzOn;
    Serial.printf("[%6lus] LUZ PASILLO: %s\n", now / 1000, luzOn ? "ENCENDIDA" : (luzHabilitada ? "APAGADA (sin movimiento)" : "APAGADA (deshabilitada desde la app)"));
  }

  // Lectura DHT22 cada 2 s: R2, R3, R4
  if (now - tMuestreo >= T_MUESTREO) {
    tMuestreo = now;
    float t = dht.readTemperature(), h = dht.readHumidity();

    if (isnan(t) || isnan(h)) {
      if (++fallasDht >= 3 && !dhtFalla) { dhtFalla = true; Serial.printf("[%6lus] DHT22: SIN LECTURA (revisa cable en D4)\n", now / 1000); }
    } else {
      if (dhtFalla) Serial.printf("[%6lus] DHT22: lectura recuperada\n", now / 1000);
      fallasDht = 0; dhtFalla = false;
      tAct = t; hAct = h;
      if (isnan(tAmb)) { tAmb = t; hAmb = h; tRef = t; }

      // El ambiente sigue cambios lentos solo en reposo
      if (!climaOn && !alarma) { tAmb += 0.02 * (t - tAmb); hAmb += 0.02 * (h - hAmb); }

      // R2: clima con histéresis según el modo elegido en la app
      if (!climaPower) climaOn = false;
      else if (climaModo == "heating") {
        if (!climaOn && t <= tAmb - CLIMA_ON_T) climaOn = true;
        else if (climaOn && t >= tAmb - climaOffT()) climaOn = false;
      } else if (climaModo == "fan") {
        if (!climaOn && h >= hAmb + CLIMA_ON_H) climaOn = true;
        else if (climaOn && h <= hAmb + CLIMA_OFF_H) climaOn = false;
      } else {
        if (!climaOn && (t >= tAmb + CLIMA_ON_T || h >= hAmb + CLIMA_ON_H)) climaOn = true;
        else if (climaOn && t <= tAmb + climaOffT() && h <= hAmb + CLIMA_OFF_H) climaOn = false;
      }

      // R3: persiana por pasos de PASO_SERVO °C
      if (persiana == CERRADA) { if (t < tRef) tRef = t; if (t >= tRef + PASO_SERVO) { persiana = ABIERTA; tRef = t; } }
      else                     { if (t > tRef) tRef = t; if (t <= tRef - PASO_SERVO) { persiana = CERRADA; tRef = t; } }

      // R4: alarma de sobretemperatura (se puede desactivar desde la app)
      if (!ALARMA_ACTIVA) alarma = false;
      else if (!alarma && t >= tAmb + ALARMA_ON_T) alarma = true;
      else if (alarma && t <= tAmb + alarmaOffT()) alarma = false;
    }
    if (dhtFalla) climaOn = false;   // sin datos, clima a estado seguro

    rele(PIN_RELE2, climaOn);

    if (climaOn != climaPrev) { climaPrev = climaOn; Serial.printf("[%6lus] CLIMA: %s\n", now / 1000, climaOn ? "ENCENDIDO" : "APAGADO"); }
    if (persiana != persianaPrev) { persianaPrev = persiana; Serial.printf("[%6lus] PERSIANA: %s (%d°)\n", now / 1000, persiana == ABIERTA ? "ABIERTA" : "CERRADA", persiana); }
    if (alarma != alarmaPrev) { alarmaPrev = alarma; Serial.printf("[%6lus] ALARMA: %s\n", now / 1000, alarma ? "SOBRETEMPERATURA" : "liberada"); }

    Serial.printf("[%6lus] T=%.1f°C HR=%.0f%% (amb %.1f°C %.0f%%) · PIR=%d · luz=%s clima=%s(%s) persiana=%d°(%s) alarma=%s · nube=%s\n",
                  now / 1000, tAct, hAct, tAmb, hAmb, pir, luzOn ? "ON" : "OFF", climaOn ? "ON" : "OFF", climaModo.c_str(),
                  servoPos, PERSIANA_AUTO ? "auto" : "manual", alarma ? "SI" : "no", plataforma ? "OK" : "--");
  }

  // Confirmación inmediata de órdenes + telemetría periódica
  if (pendLuz)   { pendLuz = false;   publicar(ID_LUZ, 0, desLuz, luzHabilitada, luzOn ? 10 : 0.5); }
  if (pendClima) { pendClima = false; publicar(ID_CLIMA, 1, desClima, climaPower, climaOn ? 1200 : 0.5); }
  if (pendNodo)  { pendNodo = false;  publicar(ID_NODO, 2, desNodo, true, 0.5); }
  static uint8_t estadoPublicado = 0;
  uint8_t estado = luzOn | (climaOn << 1) | (alarma << 2) | ((pirEst && PIR_ACTIVO) << 3);
  if (plataforma && (tTelemetria == 0 || estado != estadoPublicado || now - tTelemetria >= T_TELEMETRIA)) {
    estadoPublicado = estado; tTelemetria = now ? now : 1; publicarTodo(); }

  // LED estado y buzzer
  if (alarma)                     digitalWrite(PIN_ESTADO, (now / 100) % 2);   // rápido: alarma
  else if (dhtFalla || !plataforma) digitalWrite(PIN_ESTADO, (now / 500) % 2); // lento: sin plataforma o DHT22 sin lectura
  else                            digitalWrite(PIN_ESTADO, HIGH);              // fijo: todo OK

  static bool bzOn = false;
  bool bzQuiere = alarma && (now / 250) % 2;
  if (bzQuiere != bzOn) { bzOn = bzQuiere; buzzer(bzOn); }

  // Persiana: barrido lento, 1° cada MS_POR_GRADO, sin bloquear el resto.
  // Automática: va a CERRADA/ABIERTA según la temperatura. Manual: va al ángulo pedido desde la app.
  int destino = PERSIANA_AUTO ? persiana : ANGULO_MANUAL;
  if (servoPos != destino && now - tServo >= MS_POR_GRADO) {
    tServo = now;
    servoPos += destino > servoPos ? 1 : -1;
    moverServo(servoPos);
  }
}
