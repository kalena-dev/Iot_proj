"""Forward CF600 presentations from stdin to a paired local checkout."""
import json
import os
from pathlib import Path
import sys
import time
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from uuid import uuid4

RESCAN_GAP = 1.0


def main():
    pairing = Path(os.environ["RFID_LOCAL_PAIRING_FILE"])
    key = os.environ["RFID_API_KEY"]
    url = "http://127.0.0.1:5080/api/rfid/events"
    active = None
    presentations = {}
    for line in sys.stdin:
        observed, epc = line.split()
        observed = float(observed)
        session = pairing.read_text().strip()
        if not session:
            continue
        if session != active:
            active = session
            presentations.clear()
        now = time.monotonic()
        last_seen, scan_id, retry_at = presentations.get(epc, (0, None, 0))
        if scan_id is None or observed - last_seen >= RESCAN_GAP:
            scan_id, retry_at = str(uuid4()), 0
        if now >= retry_at:
            request = Request(url, json.dumps({
                "session": session, "epcs": [epc], "scanId": scan_id,
            }).encode(), {"Content-Type": "application/json", "X-Rfid-Key": key})
            try:
                with urlopen(request, timeout=2):
                    pass
                retry_at = float("inf")
                print(f"Accepted EPC {epc}", flush=True)
            except HTTPError as error:
                print(f"EPC {epc} rejected (HTTP {error.code})", file=sys.stderr)
                retry_at = now + 5
            except OSError as error:
                print(f"RFID delivery failed: {error}", file=sys.stderr)
                retry_at = now + 5
        presentations[epc] = (observed, scan_id, retry_at)


if __name__ == "__main__":
    main()
