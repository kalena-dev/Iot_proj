"""Forward live SDK reads to the most recently opened local demo checkout."""
import os
from pathlib import Path
import sys
import time
from urllib.error import HTTPError

from cf600_sdk import DEFAULT_SDK, inventory
from rfid_bridge import send


def main():
    pairing = Path(os.environ['RFID_LOCAL_PAIRING_FILE'])
    url = os.environ['RFID_SERVER_URL']
    key = os.environ['RFID_API_KEY']
    active = None
    seen = set()
    while True:
        try:
            for batch in inventory(DEFAULT_SDK, '/dev/ttyUSB0', include_repeats=True):
                session = pairing.read_text().strip()
                if session != active:
                    active = session
                    seen.clear()
                epc = batch[0]
                if not session or epc in seen:
                    continue
                try:
                    send(batch, url, key, session)
                except HTTPError as error:
                    if error.code not in (400, 404):
                        raise
                    print(f'Checkout rejected a tag (HTTP {error.code}). Check the mapping or reload checkout.', file=sys.stderr)
                seen.add(epc)
        except (OSError, ValueError) as error:
            print(f'Retrying local reader in two seconds: {error}', file=sys.stderr)
            time.sleep(2)


if __name__ == '__main__':
    main()
