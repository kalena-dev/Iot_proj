"""Read EPCs from one inventory cycle of the Linux CF600 SDK's CDemo.out."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys

DEFAULT_SDK = Path.home() / 'Downloads/UHF Desk Reader SDK/Sample/CDemo/Source/ARM64'
RECORD = re.compile(r'Data:((?:[0-9A-Fa-f]{2} ){12})\s*Len:(\d+) Ant:[0-9A-Fa-f]{2} RSSI:-?\d+ Channel:\d+')
DONE = "Enter 'E' to continue, Enter 'Q' to exit!"


def parse_tag(line):
    match = RECORD.fullmatch(line)
    if not match:
        raise ValueError('Malformed Data record from CDemo.out.')
    length = int(match[2])
    # The vendor sample always prints 12 bytes, even if codeLen differs.
    if not 2 <= length <= 12:
        raise ValueError(f'CDemo.out reported {length} EPC bytes but displays only 12. Use the serial adapter for longer tags.')
    return bytes.fromhex(match[1])[:length].hex().upper()


def inventory(sdk_dir, port, *, include_repeats=False):
    sdk_dir = Path(sdk_dir).expanduser().resolve()
    # CDemo uses operator>> into a 256-byte buffer for the serial device name.
    if not port or len(os.fsencode(port)) >= 256 or any(c.isspace() for c in port):
        raise ValueError('Supply a serial device path under 256 bytes without whitespace.')
    env = os.environ.copy()
    env['LD_LIBRARY_PATH'] = os.pathsep.join(filter(None, (str(sdk_dir), env.get('LD_LIBRARY_PATH'))))
    with subprocess.Popen([sdk_dir / 'CDemo.out'], cwd=sdk_dir, env=env,
                          stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True) as cli:
        try:
            # COM, device, inventory 100 reads, then quit after InventoryStop.
            cli.stdin.write(f'a\n{port}\n6\nQ\n')
            cli.stdin.close()
            finished = False
            seen = set()
            for raw in cli.stdout:
                line = raw.strip()
                if line.startswith('Data:'):
                    epc = parse_tag(line)
                    if not include_repeats and epc in seen:
                        continue
                    seen.add(epc)
                    yield [epc]
                elif 'Failed' in line:
                    raise ValueError(f'CDemo.out: {line} ({port})')
                elif line == DONE:
                    finished = True
            status = cli.wait()
            if status or not finished:
                raise ValueError(f'CDemo.out ended before completing inventory (exit {status}).')
        finally:
            if cli.poll() is None:
                cli.kill()
                cli.wait()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sdk-dir', type=Path, default=DEFAULT_SDK)
    parser.add_argument('--port', default='/dev/ttyUSB0')
    args = parser.parse_args()
    try:
        for batch in inventory(args.sdk_dir, args.port):
            print(json.dumps(batch), flush=True)
    except KeyboardInterrupt:
        return 130
    except (OSError, ValueError) as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
