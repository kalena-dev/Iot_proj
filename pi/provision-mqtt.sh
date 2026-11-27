#!/usr/bin/env bash
# Generate a NEW offline broker bundle. Never install/restart a deployed broker.
set -euo pipefail
umask 077
if [[ $# -ne 2 || ! "$1" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ || "$1" == *..* || "$1" =~ ^[0-9.]+$ ]]; then
  echo "Usage: $0 broker-dns-name NEW-output-directory" >&2
  echo 'Use a stable DNS name (for example northstar-broker.local), never an IP.' >&2
  exit 2
fi
name=$1
output=$2
if [[ -e "$output" ]]; then echo 'Output must not exist; refusing to replace keys.' >&2; exit 1; fi
command -v openssl >/dev/null
command -v mosquitto_passwd >/dev/null
mkdir -m 700 -- "$output"
output=$(cd -- "$output" && pwd)
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$output/ca.key" 2>/dev/null
openssl req -new -x509 -sha256 -days 3650 -key "$output/ca.key" -out "$output/ca.crt" \
  -subj '/CN=Northstar local MQTT CA' -addext 'basicConstraints=critical,CA:TRUE,pathlen:0' \
  -addext 'keyUsage=critical,keyCertSign,cRLSign'
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$output/server.key" 2>/dev/null
openssl req -new -sha256 -key "$output/server.key" -out "$output/server.csr" -subj "/CN=$name"
printf 'basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\nsubjectAltName=DNS:%s,DNS:localhost\n' "$name" > "$output/server.ext"
openssl x509 -req -sha256 -days 365 -in "$output/server.csr" -CA "$output/ca.crt" \
  -CAkey "$output/ca.key" -CAcreateserial -extfile "$output/server.ext" -out "$output/server.crt" 2>/dev/null
openssl verify -CAfile "$output/ca.crt" -verify_hostname "$name" "$output/server.crt" >/dev/null
# Hash passwords from a private input file: never expose passwords in process argv/logs.
: > "$output/passwords"
for role in sensorFrig1 sensorFrig2 northstar-iot northstar-fan; do
  password=$(openssl rand -hex 32)
  printf '%s:%s\n' "$role" "$password" >> "$output/passwords"
  printf 'MQTT_USER=%s\nMQTT_PASSWORD=%s\nMQTT_URL=mqtts://localhost:8883\nMQTT_SERVER_NAME=%s\nMQTT_CA_FILE=/etc/northstar/certs/ca.crt\n' \
    "$role" "$password" "$name" > "$output/$role.env"
done
unset password
mosquitto_passwd -U "$output/passwords"
cp -- "$script_dir/mosquitto.acl.example" "$output/acl"
cp -- "$script_dir/mosquitto.conf.example" "$output/northstar.conf"
chmod 600 "$output"/*
printf 'Created private bundle in %s. Nothing installed or restarted.\n' "$output"
printf 'Broker certificate SANs: %s and localhost. Use MQTT_SERVER_NAME=%s on clients.\n' "$name" "$name"
echo 'Keep ca.key offline; embed ca.crt only in firmware. Role passwords are in separate private role.env files.'
echo 'Install explicitly with: sudo bash pi/install-mqtt.sh BUNDLE_DIRECTORY'
