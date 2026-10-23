#!/usr/bin/env bash
# Render the systemd templates for the current checkout/user; never embed secrets.
# This installs units only. Enabling the hardware fan remains an explicit action.
set -euo pipefail

if [[ $# -gt 1 || ( $# -eq 1 && "$1" != "--check" ) ]]; then
  echo "Usage: $0 [--check] (validate rendered units without installing)" >&2
  exit 2
fi
check_only=${1:-}
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
if [[ ${EUID} -eq 0 ]]; then
  echo "Run as your normal service user, without sudo; installation will request sudo." >&2
  exit 1
fi
node=$(command -v node || true)
dotnet=$(command -v dotnet || true)
if [[ -z "$dotnet" && -x "${DOTNET_ROOT:-$HOME/.dotnet}/dotnet" ]]; then
  dotnet="${DOTNET_ROOT:-$HOME/.dotnet}/dotnet"
fi
python="$root/.venv/bin/python"
if [[ ! -x "$python" ]]; then python=$(command -v python3 || true); fi
if [[ -z "$node" || -z "$dotnet" || -z "$python" ]]; then
  echo "Install Node.js 20+, .NET 8 and Python first (or set DOTNET_ROOT)." >&2
  exit 1
fi
if [[ ! -f "$root/iot-server/dist/main.js" || ! -f "$root/publish/web/SmartStore.dll" ]]; then
  echo "Build iot-server and publish the web app to publish/web first; see docs/PHASE2.md." >&2
  exit 1
fi

temporary=$(mktemp -d)
trap 'rm -rf -- "$temporary"' EXIT
python3 - "$root" "$temporary" "$(id -un)" "$node" "$dotnet" "$python" <<'PY'
from pathlib import Path
import sys
root, output, user, node, dotnet, python = sys.argv[1:]
values = {'USER': user, 'PROJECT_ROOT': root, 'NODE': node, 'DOTNET': dotnet, 'PYTHON': python}
for source in (Path(root) / 'pi/systemd').glob('northstar-*.service'):
    text = source.read_text()
    for name, value in values.items():
        # Quoted systemd paths: escape literals and specifiers, including spaces.
        value = value.replace('\\', '\\\\').replace('"', '\\"').replace('%', '%%')
        text = text.replace('@' + name + '@', value)
    (Path(output) / source.name).write_text(text)
PY
if [[ "$check_only" == "--check" ]]; then
  systemd-analyze verify "$temporary"/northstar-*.service
  echo 'Rendered systemd units are valid; nothing installed or started.'
  exit 0
fi
sudo install -m 644 "$temporary"/northstar-*.service /etc/systemd/system/
sudo systemctl daemon-reload
echo 'Installed portable service units. Review with systemctl cat northstar-iot northstar-web.'
echo 'Enable sensors/web: sudo systemctl enable --now mosquitto northstar-iot northstar-web'
echo 'Only after checking motor wiring: sudo systemctl enable --now northstar-fan'
