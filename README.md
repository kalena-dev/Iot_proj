# Northstar Smart Store — Phases 1–4

ASP.NET Core/.NET 8, MongoDB, a TypeScript MQTT/email service, two ESP32/DHT11 sensors, Raspberry Pi GPIO, a CF600 reader, and a Minew MSP01 dashboard.

## What the project does

| Phase | Implementation |
|---|---|
| 1 | Staff customer registry, database notifications, blue success LED and red failure LED/buzzer |
| 2 | Four live refrigerator gauges, independent persisted thresholds, email YES/NO approval and acknowledged shared fan |
| 3 | Product management, dated deliveries, distinct RFID units, optional membership, simulated payment, loyalty and emailed receipts |
| 4 | Live inventory report, date-filtered sales/customer reports, personal receipts/spending/product-price diagrams and MSP01 widgets |

Payments are simulations. An EPC identifies an inventory unit, not an authenticated physical item. The virtual scan button is simulation; typed codes are manual entry. Neither is proof of a physical reader scan.

## Development prerequisites

- .NET 8 SDK/runtime, Node.js 20+, Python 3, and a MongoDB replica set or Atlas.
- HTTPS with a certificate trusted by the browser. Network HTTP never serves account forms; cookies require HTTPS.
- SMTP on **587 with STARTTLS** for account verification, recovery and receipts. IMAP993 receives refrigerator approvals.
- A certificate-validated MQTT broker with separate sensor, IoT and fan identities for refrigeration.

Copy `.env.example` to a private `.env` and configure it; do not leave example passwords/control keys. Generate the shared local API key privately with `openssl rand -hex 32`.

```bash
dotnet restore SmartStore.sln
npm --prefix iot-server ci
npm --prefix iot-server run build
dotnet dev-certs https --trust
dotnet run --project src/SmartStore --launch-profile https
```

Open `https://localhost:7051`. Trusting a development certificate is a local development step, not a valid public deployment certificate. GPIO is log-only off Linux; this is not physical verification.

The web application can be used without starting refrigeration. A valid `IOT_API_KEY` is required when using its refrigeration proxy; the IoT service refuses missing/example keys at startup. `/health` reports database connectivity only.

## Accounts, membership and staff access

Customers register their **name, email and password**, verify the emailed code, and receive an automatically generated membership number. My account shows membership, points and personal reports. Checkout also supports nonmembers; membership is optional, not a prerequisite for buying.

New purchases, loyalty and coupons belong to immutable account IDs, not current ownership of an email address. Existing email-only receipts and balances remain preserved for staff inspection but are not automatically inherited by a newly registered mailbox holder. Any legacy transfer requires staff to verify the original receipt/reference and the person's identity independently; email access alone is insufficient.

Staff access requires explicit provisioning:

1. Register and verify the staff member's account over HTTPS.
2. Read that account's ID from My account/Account security.
3. Put its ID in the private `AUTH_ADMIN_ACCOUNT_IDS` allowlist.
4. From the protected server environment, run:

   ```bash
   dotnet run --project src/SmartStore -- --provision-staff ACCOUNT_ID
   ```

5. Sign in again with password and the fresh emailed staff code.

Email-only allowlists no longer grant authority. Adding an ID cannot elevate an already-open customer session. Staff verification expires after 30 minutes. Password recovery, global sign-out and account disable revoke prior session stamps; database validation fails closed. Recovery responses do not disclose whether an address is registered. Preserve mailbox access and do not share staff sessions with the checkout browser.

## Inventory and checkout

Register a product once, then receive deliveries. Each tagged physical unit has its own EPC, **at most 24 hexadecimal characters in whole bytes**. UPC identifies the product type. Untagged deliveries are also supported.

Checkout validates prices, quantities and unit eligibility on the server. Approved sales atomically update stock, exact tagged units, receipt, coupon and account-owned loyalty. Declines change no stock or points. Repeated submission of the same logical attempt returns its original result instead of selling twice.

Removing a tagged unit selects that unit's EPC; reducing a product quantity must not silently retire a different physical tag. Barcode sales consume untagged stock first. Reconciliation of historical guessed tags cannot assign a sale to a later delivery. Correction-only receiving does not add stock or invent a delivery.

Products are retired rather than erasing their receiving/tag history. Reports keep stable product IDs and historical prices/names so changing a UPC cannot split or merge product histories. See [the inventory contract](docs/PHASE3-inventory.md).

The reader is explicitly paired to its browser/workflow. Opening Receive in another tab cannot silently take over a paired checkout. See [CF600 setup](docs/phase3-rfid.md).

## Integrated wiring — power off before changing it

The old Phase 1 LED pins BCM17/27 are now motor inputs. **Move both LEDs before enabling the integrated mapping.** All numbers below are BCM GPIO, not physical header numbers.

| Component | GPIO | Physical pin | Wiring |
|---|---:|---:|---|
| Blue LED | 5 | 29 | GPIO → 220–330 Ω → anode; cathode → GND |
| Red LED | 6 | 31 | GPIO → 220–330 Ω → anode; cathode → GND |
| Buzzer driver | 22 | 15 | GPIO → 1 kΩ → NPN base; emitter → GND; collector → buzzer−; buzzer+ → rated supply |
| Fan L293D EN1 | 27 | 13 | Enable with 10 kΩ pull-down |
| Fan L293D IN2 | 17 | 11 | Direction input; IN1 held LOW |
| Common ground | GND | 6 | Pi, driver and motor-supply grounds |

Never drive a motor or the kit's 5 V buzzer directly from GPIO. Use a properly rated external motor supply and driver, verify transistor pinouts, and retain a physical power cutoff. A status acknowledgement confirms GPIO output, not mechanical rotation.

The integrated defaults enable LEDs on 5/6. The fan remains disabled until wiring and cutoff are verified. If your breadboard still uses old pins, keep `GPIO__ENABLED=false` until rewired; do not share output owners.

## Secure Raspberry Pi deployment

Use a private lab network even with TLS. Do not expose MongoDB, the local IoT API or the broker's administrative interface publicly.

1. Install system-wide Node/.NET/Python, Mosquitto and its clients, OpenSSL, Python GPIO dependencies, and a trusted HTTPS reverse proxy.
2. Provision a **new offline** MQTT bundle using a stable certificate DNS name:

   ```bash
   bash pi/provision-mqtt.sh northstar-broker.local /private/new-mqtt-bundle
   sudo bash pi/install-mqtt.sh /private/new-mqtt-bundle
   ```

   The installer refuses existing destinations and does not restart the broker. Review existing Mosquitto includes, remove old plaintext listeners/shared credentials, then explicitly restart it. Never distribute `ca.key`; clients need only `ca.crt`.

3. Build/publish and install the isolated services:

   ```bash
   npm --prefix iot-server ci
   npm --prefix iot-server run build
   dotnet publish src/SmartStore/SmartStore.csproj -c Release -o publish/web
   sudo bash pi/install-services.sh --install
   ```

4. Fill `/etc/northstar/web.env`, `iot.env` and `fan.env` with **only each role's settings**. They are root-owned, role-readable `0640`; services use different OS users. Fan must not receive MongoDB/mail secrets. Use separate database users/custom roles for web and IoT; grant only the collections/actions each needs. See [Phase 2 deployment](docs/PHASE2.md).
5. Put HTTPS in front of the loopback web listener. `pi/Caddyfile.example` shows local CA-backed HTTPS. Set the real host, trust its CA certificate on every client, and configure `Security__TrustedProxies__0=127.0.0.1` only when that is the actual trusted proxy. Do not forward untrusted scheme headers. For a public domain, use a publicly trusted certificate.
6. Start web/IoT only after configuration is complete. The installer never starts or enables the physical fan:

   ```bash
   sudo systemctl enable --now mosquitto northstar-iot northstar-web
   # Only after wiring, permissions and independent cutoff are verified:
   # sudo systemctl enable --now northstar-fan
   ```

`NORTHSTAR_CONFIG_FILE` is authoritative for deployed web/IoT roles: it must exist and no project/home fallback is read. Local development without it keeps process → `MONGODB_ENV_FILE` → project `.env` → private user configuration precedence. Both services use `MONGODB_DATABASE`.

See [refrigeration wiring, TLS/ACLs and demonstration](docs/PHASE2.md), [email setup](docs/EMAIL_SETUP.md), and [the security audit and remediation record](docs/SECURITY-AUDIT.md).

## Minew MSP01

Configure the MSP01's humidity/temperature, photosensitive and PIR slots using the [assignment's vendor guide](https://reelyactive.github.io/diy/minew-msp01-config/). Run Pareto Anywhere **and a BLE acquisition source**, such as the Pi's `barnowl-hci` forwarder; starting the middleware alone does not collect radio data.

Set `MSP01_DEVICE_ID`, `MSP01_DEVICE_TYPE` and `MSP01_PARETO_URL` privately. Plain HTTP is permitted only for loopback middleware. Remote middleware requires verified HTTPS. `/Sensor` displays temperature, humidity, light and motion; missing/future/invalid observation times are unknown rather than live.

## Verification

Run checks only against synthetic data and an isolated **local replica set**, never a store/Atlas database:

```bash
SMARTSTORE_TEST_MONGODB_URI='mongodb://127.0.0.1:27019/?replicaSet=northstar-test' \
  dotnet test tests/SmartStore.Tests/SmartStore.Tests.csproj -c Release
node --test tests/checkout/checkout.test.cjs tests/ui/safety.test.cjs
npm --prefix iot-server run build
npm --prefix iot-server test
python3 -m unittest discover -s pi -p 'test_*.py'
bash -n pi/install-services.sh pi/provision-mqtt.sh pi/install-mqtt.sh pi/start-checkout.sh firmware/flash-sensor.sh
```

Database tests refuse remote hosts and use randomly named disposable databases. Without the local test URI they skip, so a green non-database run is not persistence proof. Check dependency advisories with `npm --prefix iot-server audit` and `dotnet list src/SmartStore/SmartStore.csproj package --vulnerable --include-transitive`; do not disable auditing to hide a warning.

Compile the firmware twice, once for each fridge, before flashing. Hardware-free tests cannot establish RF reads, motor movement, wiring, Wi-Fi/mDNS at the venue, external inbox delivery, or MSP01 BLE acquisition.

### Final demonstration

Show customer insertion and both indicator outcomes; two changing sensor streams/four gauges; saved separate thresholds; a received alert; NO doing nothing; YES and confirmed motor movement; immediate OFF and the cutoff; product delivery; real multi-tag checkout/removal; guest and member payment; one receipt/stock deduction on resubmission; receipt email; admin/customer reports; all four MSP01 readings. Explain the schema and security boundaries.

### Required group task table

The repository does not establish every member's actual assignment. Supply real names and demonstrated responsibilities before submission; do not invent authorship. The inventory document previously identified Jonathan, and checkout/account notes identified Renz/Fahad, but those notes do not establish the complete final allocation.

| Required responsibility | Student(s) | Demonstration evidence |
|---|---|---|
| Customer registry and LED/buzzer circuit | Group must supply actual names | Successful and failed insert |
| ESP32/DHT11, MQTT/TLS and Pi fan | Group must supply actual names | Two streams, denied cross-role command, YES/OFF |
| Products, receiving and RFID | Group must confirm actual names | Dated delivery and physical multi-tag checkout |
| Accounts, payment/loyalty and receipts | Group must confirm actual names | Member/guest flow and receipt email |
| Admin/customer reporting and MSP01 | Group must supply actual names | Filtered reports and four BLE widgets |
