#!/usr/bin/env bash
# --check is read-only except temporary rendering; --install never starts a unit.
set -euo pipefail
if [[ $# -ne 1 || ( "$1" != "--check" && "$1" != "--install" ) ]]; then
  echo "Usage: bash $0 --check|--install (install requires root; never enables fan)" >&2
  exit 2
fi
mode=$1
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
destination=/opt/northstar
node=$(command -v node || true)
dotnet=$(command -v dotnet || true)
python=$(command -v python3 || true)
if [[ -z "$node" || -z "$dotnet" || -z "$python" ]]; then
  echo 'Install system-wide Node.js 20+, .NET 8 and Python 3 first.' >&2
  exit 1
fi
for executable in "$node" "$dotnet" "$python"; do
  case $(readlink -f "$executable") in
    /home/*|/root/*) echo 'Service runtimes must be installed outside protected home directories.' >&2; exit 1 ;;
  esac
done
if [[ ! -f "$root/iot-server/dist/main.js" || ! -d "$root/iot-server/node_modules" || ! -f "$root/publish/web/SmartStore.dll" ]]; then
  echo 'Build iot-server with dependencies and publish web to publish/web before installing.' >&2
  exit 1
fi
temporary=$(mktemp -d)
trap 'rm -rf -- "$temporary"' EXIT
"$python" - "$root" "$temporary" "$destination" "$node" "$dotnet" "$python" <<'PY'
from pathlib import Path
import sys
root, output, destination, node, dotnet, python = sys.argv[1:]
values = {'PROJECT_ROOT': destination, 'NODE': node, 'DOTNET': dotnet, 'PYTHON': python}
for source in (Path(root) / 'pi/systemd').glob('northstar-*.service'):
    text = source.read_text()
    for name, value in values.items():
        value = value.replace('\\', '\\\\').replace('"', '\\"').replace('%', '%%')
        text = text.replace('@' + name + '@', value)
    (Path(output) / source.name).write_text(text)
PY
if [[ "$mode" == "--check" ]]; then
  # Environment files/users need not exist yet; verify the rendered syntax.
  systemd-analyze verify "$temporary"/northstar-*.service
  echo 'Rendered units checked; no users, settings, files or services changed.'
  exit 0
fi
if [[ $EUID -ne 0 ]]; then echo 'Use sudo bash pi/install-services.sh --install.' >&2; exit 1; fi
getent group gpio >/dev/null || { echo 'Create the OS gpio device-access group first.' >&2; exit 1; }
# Service users have no shell and cannot read one another's credential files.
for role in web iot fan; do
  user="northstar-$role"
  if ! getent passwd "$user" >/dev/null; then
    useradd --system --user-group --home-dir "/var/lib/$user" --no-create-home --shell /usr/sbin/nologin "$user"
  fi
done
install -d -o root -g root -m 755 /etc/northstar "$destination"
for role in web iot fan; do
  install -d -o root -g "northstar-$role" -m 750 "$destination/$role"
  if [[ ! -e "/etc/northstar/$role.env" ]]; then
    install -o root -g "northstar-$role" -m 640 /dev/null "/etc/northstar/$role.env"
  else
    echo "Preserving existing /etc/northstar/$role.env contents; enforcing isolated ownership."
  fi
  chown "root:northstar-$role" "/etc/northstar/$role.env"
  chmod 640 "/etc/northstar/$role.env"
done
cp -R -- "$root/publish/web/." "$destination/web/"
install -d -m 750 "$destination/iot/dist"
cp -R -- "$root/iot-server/dist/." "$destination/iot/dist/"
cp -R -- "$root/iot-server/node_modules" "$destination/iot/"
install -o root -g northstar-fan -m 640 "$root/pi/fan_controller.py" "$destination/fan/"
for role in web iot; do
  chown -R "root:northstar-$role" "$destination/$role"
  chmod -R g+rX,o-rwx "$destination/$role"
done
if [[ ! -s /etc/northstar/fan.env ]]; then
  printf 'FAN_SIMULATE=true\nFAN_WIRING_CONFIRMED=false\nFAN_GPIO=27\nFAN_IN2_GPIO=17\nFAN_MAX_RUN_SECONDS=300\n' > /etc/northstar/fan.env
fi
install -o root -g root -m 644 "$temporary"/northstar-*.service /etc/systemd/system/
systemctl daemon-reload
echo 'Installed isolated units and application files; nothing started or enabled.'
echo 'Supply ONLY role-specific settings in /etc/northstar/{web,iot,fan}.env (root:role 0640).'
echo 'Configure HTTPS/trusted proxy for web, distinct database roles, MQTT CA/server name and role passwords.'
echo 'Install pi/requirements.txt into the selected system Python environment before starting fan.'
echo 'Review settings, then explicitly: sudo systemctl enable --now northstar-iot northstar-web'
echo 'Fan stays disabled and simulated. Verify independent cutoff and wiring before explicitly enabling physical mode/service.'
