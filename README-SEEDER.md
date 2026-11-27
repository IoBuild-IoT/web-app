# Seeder de la base de datos (demo con ESP32)

Deja la base lista para probar el prototipo: cuentas de prueba, una unidad y los tres
dispositivos que controla la placa (IDs **16, 17 y 18**, los mismos de `secrets.h`).

Corre solo al iniciar la API (`iobuild-api`). No hay que ejecutar ningún comando aparte.

## Qué hace, en orden

Cada paso revisa primero si ya está hecho. Si ya está, lo salta y sigue con el siguiente.
Por eso se puede reiniciar la API todas las veces que quieras: nunca duplica datos.

| # | Regla | Si no se cumple |
|---|---|---|
| 1 | Existe la base `iobuild` con sus tablas | La crea (`EnsureCreated` + columnas nuevas) |
| 2 | Hay planes de pago | Crea Starter, Professional y Enterprise |
| 3 | Existe la cuenta **builder** de prueba | La registra (mismo flujo que la pantalla de registro) |
| 4 | El builder tiene perfil | Lo crea |
| 5 | El builder tiene suscripción activa | Le asigna el plan más alto |
| 6 | Hay una unidad para el hardware | Si algún dispositivo 16/17/18 ya está en una unidad, usa esa. Si no, crea el proyecto **IoBuild Demo** y la unidad **101** |
| 7 | La unidad tiene dueño | Si está libre, la asigna al email del **owner** de prueba |
| 8 | Existen los dispositivos 16, 17 y 18 | Crea los que falten con ese ID exacto y el tipo correcto (ver tabla abajo) |
| 9 | Existe la cuenta **owner** de prueba | La registra; el registro le enlaza la unidad y sus dispositivos |
| 10 | El owner tiene perfil | Lo crea |

Los pasos 1 y 2 corren siempre (ya existían). Los pasos 3 a 10 solo corren con
`DEMO_SEED_ENABLED=true`.

| ID | Tipo | Qué controla en la placa |
|---|---|---|
| 16 | `AirConditioner` | Relé 2 · LED azul (clima) |
| 17 | `SmartLight` | Relé 1 · LED amarillo (luz pasillo) |
| 18 | `EdgeNode` | Umbrales, PIR, alarma y **servo (persiana)** |

### Casos especiales (no rompe nada)

- **La unidad ya tiene otro dueño** (como pasa hoy en el VPS): crea los dispositivos que
  faltan para ese dueño y **no** crea el owner de prueba. El log dice `owner skipped`.
- **El ID ya existe pero es de otro tipo u otra unidad**: no lo toca y escribe un
  `WARNING`. Hay que corregirlo a mano o cambiar los IDs en `secrets.h`.
- **La unidad ya tiene un dispositivo de ese tipo con otro ID**: igual, `WARNING` y no
  lo toca, porque cada unidad acepta un solo dispositivo de cada tipo.

## Cómo activarlo

Variables de entorno (en `.env` local o en Environment de Dokploy):

```env
DEMO_SEED_ENABLED=true
DEMO_BUILDER_PASSWORD=<clave de 8+ caracteres>
DEMO_OWNER_PASSWORD=<clave de 8+ caracteres>
# Opcionales (estos son los valores por defecto)
DEMO_BUILDER_EMAIL=builder.demo@iobuild.test
DEMO_OWNER_EMAIL=owner.demo@iobuild.test
```

Las claves **no** están en el repo. Si falta alguna, el seeder no hace nada y lo avisa en el log.

Después reinicia la API:

- **Local:** `docker compose up -d iobuild-api`
- **Dokploy:** guarda las variables y haz **Redeploy**.

También acepta `DemoSeed__UnitNumber`, `DemoSeed__ClimateId`, `DemoSeed__LightId` y
`DemoSeed__NodeId` si alguna vez cambias los IDs del firmware.

## Cómo ver qué hizo

```bash
docker logs iobuild-api-iot 2>&1 | grep DemoSeed
```

Ejemplo en una base vacía:

```text
[DemoSeed] builder builder.demo@iobuild.test created (id 1).
[DemoSeed] builder profile created.
[DemoSeed] builder subscription created (plan Enterprise).
[DemoSeed] unit 101 created (id 1) in project IoBuild Demo.
[DemoSeed] unit 101 assigned to owner.demo@iobuild.test.
[DemoSeed] device 16 AirConditioner created in unit 101.
[DemoSeed] device 17 SmartLight created in unit 101.
[DemoSeed] device 18 EdgeNode created in unit 101.
[DemoSeed] owner owner.demo@iobuild.test created (id 2).
[DemoSeed] owner profile created.
[DemoSeed] done.
```

En el segundo arranque solo sale `[DemoSeed] done.` porque todo ya está.

## Después del seed

1. Pon `HARDWARE_DEVICE_IDS=16,17,18` para que el simulador no mande datos falsos a la placa.
2. Entra con el owner, ve a **Unit 101** y deben salir las tres tarjetas: clima, luz y Edge Node.
3. Cuando ya no lo necesites, vuelve a `DEMO_SEED_ENABLED=false`. Los datos se quedan.

Código: [`backend/src/IoBuild.Api/Persistence/DemoSeeder.cs`](backend/src/IoBuild.Api/Persistence/DemoSeeder.cs).
