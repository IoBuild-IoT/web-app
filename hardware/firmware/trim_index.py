"""Quita del índice de Espressif las herramientas que un ESP32 clásico (Xtensa) no usa al compilar.

Fuera: toolchain RISC-V, librerías de los otros chips y depuradores (~1.6 GB menos de descarga).
Para compilar para ESP32-S2/S3/C3/C6..., quita este paso del Dockerfile.
"""
import json
import sys

KEEP = {"esp-x32", "esp32-libs", "esptool_py", "mkspiffs", "mklittlefs", "dfu-util"}

source, target, version = sys.argv[1:4]
with open(source) as handle:
    index = json.load(handle)
platform = next(p for p in index["packages"][0]["platforms"] if p["version"] == version)
platform["toolsDependencies"] = [d for d in platform["toolsDependencies"] if d["name"] in KEEP]
missing = KEEP - {d["name"] for d in platform["toolsDependencies"]}
assert not missing, f"el core {version} ya no trae: {missing}"
with open(target, "w") as handle:
    json.dump(index, handle)
