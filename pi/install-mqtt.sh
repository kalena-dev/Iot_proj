#!/usr/bin/env bash
# Explicit initial provisioning only; never overwrite existing broker settings.
set -euo pipefail
if [[ $# -ne 1 || $EUID -ne 0 ]]; then echo "Usage: sudo bash $0 BUNDLE_DIRECTORY" >&2; exit 2; fi
bundle=$1
for entry in ca.crt server.crt server.key passwords acl northstar.conf; do
  [[ -f "$bundle/$entry" ]] || { echo "Missing bundle file: $entry" >&2; exit 1; }
done
getent passwd mosquitto >/dev/null
openssl verify -CAfile "$bundle/ca.crt" "$bundle/server.crt" >/dev/null
for destination in /etc/mosquitto/northstar /etc/mosquitto/conf.d/northstar.conf /etc/northstar/certs/ca.crt; do
  if [[ -e "$destination" ]]; then echo "Refusing to overwrite $destination; rotate existing settings manually." >&2; exit 1; fi
done
install -d -m 755 /etc/northstar /etc/northstar/certs /etc/mosquitto/conf.d
install -d -o root -g mosquitto -m 750 /etc/mosquitto/northstar
install -o root -g root -m 644 "$bundle/ca.crt" /etc/northstar/certs/ca.crt
install -o root -g mosquitto -m 640 "$bundle/ca.crt" "$bundle/server.crt" "$bundle/server.key" "$bundle/passwords" "$bundle/acl" /etc/mosquitto/northstar/
install -o root -g root -m 644 "$bundle/northstar.conf" /etc/mosquitto/conf.d/northstar.conf
echo 'Installed initial TLS/ACL files; broker has NOT been started/restarted.'
echo 'Remove any old plaintext listeners and shared credentials, review effective Mosquitto config, then explicitly restart mosquitto.'
echo 'Copy only the appropriate role credentials into /etc/northstar/iot.env and fan.env; embed sensor roles separately in firmware secrets.h.'
