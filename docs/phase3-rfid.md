# Phase 3 RFID — explicit reader pairing

The CF600 is a UHF inventory reader. USB/raw serial reports are not keyboard emulation; choose the transport actually configured on the classroom reader. This project supports a vendor-CLI bridge, a separate raw-serial adapter, manual codes and an explicitly labelled simulation. They are not interchangeable proof of hardware operation.

## Single-till Pi launcher

Use a private configuration file containing the real MongoDB connection, account SMTP and other web settings. Provide a certificate trusted by both browser and Python for the loopback HTTPS name:

```bash
export MONGODB_ENV_FILE=/private/path/checkout.env
export ASPNETCORE_Kestrel__Certificates__Default__Path=/private/path/localhost.pfx
export ASPNETCORE_Kestrel__Certificates__Default__Password='enter-privately'
export RFID_SERVER_URL=https://localhost:5081
# For a private CA, Python/curl need its public certificate:
export SSL_CERT_FILE=/private/path/ca.crt
bash pi/start-checkout.sh
```

Do not type real passwords into shared shell history; use a private environment file or masked local prompt. Never disable certificate verification. The launcher uses HTTPS, restores advisory checks, creates a fresh bridge key, starts same-user checkout/reader services, and prints the URL. It does not automatically pair a newly opened page.

The vendor CLI is externally provisioned under the SDK path expected by `pi/cf600_sdk.py`. Confirm that installed binary, architecture and vendor version separately. Do not run two reader applications on the same device.

## Pair, use, release

1. Open Checkout over trusted HTTPS.
2. Explicitly pair its reader session using the page control.
3. Present the delivery's registered EPCs. Distinct eligible tags add separate units; repeated readings do not add quantity twice.
4. Remove the exact unwanted tagged unit using its EPC entry.
5. Release the active reader before pairing a different workflow, such as Receive.

Opening another tab/receiving page cannot silently replace an active pairing. Sessions are scoped to the browser owner, till and workflow; reload can reuse/reclaim allocations. Expired or closed sessions reject new events.

The hardware bridge accepts only actual registered, eligible items for checkout. A confirmed-sold tag or UPC alias is not accepted as a new RFID unit. Receiving can register new EPCs; a sold-by-guess tag is handled only through the inventory reconciliation rules. EPCs are at most 24 hex characters in complete bytes.

## Separate bridge / hardened service deployment

The hardened `northstar-web` OS user is not the normal Pi operator. Do not give the reader process its database/mail credential file just to access a pairing token. Explicitly pair the page, transfer only the private session capability and bridge key, and use the standard input bridge:

```bash
export RFID_SERVER_URL=https://your-trusted-store-host
read -r -s -p 'Bridge key: ' RFID_API_KEY; echo
export RFID_API_KEY
read -r -s -p 'Paired session: ' RFID_SESSION; echo
export RFID_SESSION
set -o pipefail
python3 -u pi/cf600_reader.py | python3 -u pi/rfid_bridge.py
```

Configure the server's Reader mode, unique till ID and strong private `Rfid__ApiKey` consistently. A remote bridge must use verified HTTPS. An SSH tunnel is another transport option, but it does not excuse certificate-name validation or page/session ownership checks. Never copy another user's hardcoded machine path.

For the externally supplied ARM64 vendor demo, `pi/rfid_bridge.py --sdk` uses the vendor adapter. Reader-only output and complete identifiers must first be inspected on the actual Pi. The CLI's output/identifier limitations are vendor-specific; do not truncate a longer tag to manufacture a matching EPC.

A Windows/manual transfer of a code copied from the vendor GUI must be marked **manual**, not reader-origin. The bridge carries provenance; an identifier by itself never authenticates a tag or proves a physical scan.

## Troubleshooting

- Check which physical port/interface exists, reader permissions and whether another process owns it.
- A page reload/closed pairing requires the correct current capability. Release the active workflow before switching.
- Unknown or sold tags should be refused, not mapped automatically to arbitrary stock.
- Check the local key, session, HTTPS trust and tunnel before altering RF settings.
- Never make a device world-writable or enable an insecure network listener as a shortcut.
- No response means no reading; adapters must not fabricate fallback EPCs.

The optional raw-serial parser checks framing/status/length and identifier bounds. The repository does not establish the vendor response-CRC algorithm, so it does not claim CRC validation or tag authenticity. Resolve that against the actual vendor protocol before relying on that adapter for corruption detection.

## Verification

```bash
dotnet test tests/SmartStore.Tests/SmartStore.Tests.csproj --filter FullyQualifiedName~RfidTests
node --test tests/checkout/checkout.test.cjs
python3 -m unittest discover -s pi -p 'test_cf600*.py'
python3 -m unittest discover -s pi -p test_rfid_bridge.py
bash -n pi/start-checkout.sh
```

These cover session/owner isolation, explicit pairing, replay/limits, parser and cart behavior—not physical RF acquisition. The final demonstration needs multiple real tags, selected-unit removal, correct stock/receipt/points after payment, and verified reader-to-browser delivery on the actual equipment.
