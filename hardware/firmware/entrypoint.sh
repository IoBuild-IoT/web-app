#!/bin/sh
# compile            -> compila el sketch montado en /sketch y deja los .bin en /out
# flash <puerto>     -> compila y sube a la placa (solo Linux: docker run --device <puerto>)
set -eu
FQBN="${FQBN:-esp32:esp32:esp32}"
WORK=/tmp/iobuild_edge_node   # arduino-cli exige que la carpeta se llame igual que el .ino

[ -f /sketch/secrets.h ] || { echo "Falta secrets.h: copia secrets.example.h a secrets.h y complétalo." >&2; exit 1; }
rm -rf "$WORK" && cp -R /sketch "$WORK"
arduino-cli compile --fqbn "$FQBN" --output-dir /out "$WORK"
echo "Binarios en /out:" && ls -1 /out

if [ "${1:-compile}" = flash ]; then
  arduino-cli upload --fqbn "$FQBN" -p "${2:?indica el puerto, por ejemplo /dev/ttyUSB0}" --input-dir /out "$WORK"
fi
