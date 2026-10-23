#!/usr/bin/env bash
# Build/upload each refrigerator separately; never edit credentials between boards.
set -euo pipefail

if [[ $# -ne 2 || ( "$1" != "Frig1" && "$1" != "Frig2" ) ]]; then
  echo "Usage: $0 Frig1 /dev/ttyUSB0  (or Frig2 /dev/ttyUSB1)" >&2
  exit 2
fi

topic=$1
port=$2
firmware_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
sketch="$firmware_dir/FridgeSensor"
build_dir="$firmware_dir/build/$topic"
cli=${ARDUINO_CLI:-arduino-cli}
fqbn=${FQBN:-esp32:esp32:esp32}

if [[ ! -r "$sketch/secrets.h" ]]; then
  echo "Copy $sketch/secrets.example.h to secrets.h and configure Wi-Fi/MQTT first." >&2
  exit 1
fi
if [[ ! -c "$port" ]]; then
  echo "Serial port not found: $port (check arduino-cli board list)." >&2
  exit 1
fi

# Binaries contain Wi-Fi/MQTT credentials. Keep them private and out of Git.
umask 077
mkdir -p "$build_dir"
chmod 700 "$firmware_dir/build" "$build_dir"
printf 'Building %s with DHT11 on GPIO26; uploading to %s\n' "$topic" "$port"
"$cli" compile --fqbn "$fqbn" --build-path "$build_dir" \
  --build-property "compiler.cpp.extra_flags=-DFRIDGE_TOPIC=\"${topic}\" -DDHT_PIN=26" \
  "$sketch"
"$cli" upload --fqbn "$fqbn" --port "$port" --input-dir "$build_dir" \
  --upload-property upload.speed=460800 "$sketch"
