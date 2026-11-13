#!/usr/bin/env bash
# Start the single-till checkout and reader as user services on the Pi.
set -euo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
cd "$repo"
dotnet=$(command -v dotnet || echo "$HOME/.dotnet/dotnet")
export MONGODB_ENV_FILE="${MONGODB_ENV_FILE:-$HOME/.config/northstar/checkout-atlas.env}"
[[ -r "$MONGODB_ENV_FILE" ]] || { echo 'Set MONGODB_ENV_FILE to your private Atlas configuration.' >&2; exit 1; }
export MongoDb__DatabaseName="${MongoDb__DatabaseName:-smart_store}"
export Rfid__Mode=Reader
export Rfid__ApiKey="$(openssl rand -hex 32)"
export RFID_API_KEY="$Rfid__ApiKey"
export RFID_SERVER_URL=http://127.0.0.1:5080
export Security__UseHttpsRedirection=false
export Gpio__Enabled="${Gpio__Enabled:-false}"
umask 077
state="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/smartstore-checkout"
mkdir -p "$state"
export RFID_LOCAL_PAIRING_FILE="$state/pairing"
"$dotnet" build src/SmartStore -p:NuGetAudit=false --disable-build-servers -p:UseSharedCompilation=false
if [[ ! -c /dev/ttyUSB0 ]] && lsusb -d 0483:5750 >/dev/null; then
  sudo modprobe usbserial vendor=0x0483 product=0x5750 && udevadm settle
fi
if [[ ! -c /dev/ttyUSB0 ]]; then echo 'Waiting for the CF600. Manual checkout is available.'; fi
for service in smartstore-poc-reader smartstore-poc-web; do
  if systemctl --user is-active --quiet "$service"; then systemctl --user stop "$service"; fi
done
: > "$RFID_LOCAL_PAIRING_FILE"
# The inventory EPC is always available. Pass any extra EPC-to-UPC aliases set by the operator.
aliases=()
for variable in ${!Rfid__EpcProducts__@}; do aliases+=("--setenv=$variable"); done
systemd-run --user --collect --unit=smartstore-poc-web --working-directory="$repo/src/SmartStore" \
  --setenv=MONGODB_ENV_FILE --setenv=MongoDb__DatabaseName --setenv=Rfid__Mode --setenv=Rfid__ApiKey \
  --setenv=RFID_LOCAL_PAIRING_FILE --setenv=Gpio__Enabled --setenv=Security__UseHttpsRedirection "${aliases[@]}" \
  "$dotnet" bin/Debug/net8.0/SmartStore.dll --urls "$RFID_SERVER_URL"
systemd-run --user --collect --unit=smartstore-poc-reader --working-directory="$repo" \
  --setenv=RFID_LOCAL_PAIRING_FILE --setenv=RFID_SERVER_URL --setenv=RFID_API_KEY \
  /usr/bin/python3 pi/local_checkout_reader.py
curl -fsS --retry 10 --retry-connrefused --retry-delay 1 --max-time 15 "$RFID_SERVER_URL/health"
echo
echo "Open $RFID_SERVER_URL/Checkout"
