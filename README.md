# Northstar Smart Store — Phases 1 & 2

A C#/.NET 8 IoT application for the Vanier College **420-521-VA** final project. It captures customer information in a Razor Pages web form, saves it to MongoDB Atlas, and signals the result through Raspberry Pi 5 GPIO outputs.

## Phase 2: refrigeration dashboard

**[Complete lab setup, wiring, architecture, requirement checklist and task table →](docs/PHASE2.md)**

The existing store now includes `/Refrigerators`: four live temperature/humidity gauges, independent MongoDB-backed thresholds, email alerts and YES-reply approval, system notifications, and an acknowledged Raspberry Pi motor output. The TypeScript MQTT service matches the supplied draw.io architecture; both ESP32 boards publish DHT11 measurements on `Frig1` and `Frig2`, using each board's **GPIO26**. Boards automatically discover the local MQTT broker over mDNS, so server IP changes do not require reflashing. Classic ESP32s require **2.4 GHz WiFi**.

New components: `iot-server/` (TypeScript MQTT/MongoDB/SMTP/IMAP), `firmware/FridgeSensor/` (ESP32 sketch), and `pi/` (motor driver adapter and deployment templates). Real sensors, motor wiring and SMTP/IMAP credentials must be configured for the lab. Simulation is explicitly labeled and never presented as physical motor operation.

## Phase 1 architecture decisions

| Layer | Choice | Responsibility |
|---|---|---|
| Presentation | ASP.NET Core Razor Pages | Responsive customer form, validation, notifications, recent records |
| Application | C# / .NET 8 | Request handling, normalization, error handling, dependency injection |
| Data communication | MongoDB .NET Driver over TLS | Sends customer documents to MongoDB Atlas |
| Database | MongoDB Atlas | `smart_store.customers`, with a unique email index |
| Device output | `System.Device.Gpio` | Blue success LED; red failure LED and active/passive buzzer |

### Data flow

1. **Capture:** Razor Pages validates name, email, telephone number, and Canadian address.
2. **Communicate:** the server normalizes the fields and inserts a MongoDB document.
3. **Present:** the browser displays a notification and recent records; GPIO indicates success or failure.

The database and hardware layers are behind `ICustomerRepository` and `IStatusIndicator`, so later phases can replace or extend them without changing the page.

## Customer document design

```json
{
  "_id": "ObjectId",
  "firstName": "Jordan",
  "lastName": "Lee",
  "email": "jordan@example.com",
  "phone": "514-555-0182",
  "address": {
    "street": "821 Sainte-Catherine Street W",
    "city": "Montreal",
    "province": "QC",
    "postalCode": "H3G 1M8",
    "country": "Canada"
  },
  "createdAtUtc": "ISODate"
}
```

The application creates these indexes before the first insert:

- `ux_customers_email`: unique ascending index, preventing duplicate customers by normalized email.
- `ix_customers_createdAtUtc`: descending index for the recent-customer view.

## Breadboard wiring (Raspberry Pi 5)

The configured numbers are **BCM GPIO numbers**, not header positions. The Phase 1 GPIO output code drives only the two LEDs and buzzer; it does not read or power an RFID reader. A kit's wiring must match the GPIO numbers below.

**Phase 2 L293D fan:** GPIO27 is now EN1 and GPIO17 is IN2. Set `GPIO__ENABLED=false` to disable these old status outputs; never let the LED code and fan controller share the pins. See [the L293D wiring and configuration](docs/PHASE2.md#raspberry-pi--dc-motor-l293d).

| Component | GPIO | Physical pin | Connection |
|---|---:|---:|---|
| Blue LED | BCM 17 | 11 | GPIO → 220–330 Ω resistor → LED anode; LED cathode → GND rail |
| Red LED | BCM 27 | 13 | GPIO → 220–330 Ω resistor → LED anode; LED cathode → GND rail |
| Freenove buzzer (active or passive) | BCM 22 | 15 | GPIO → 1 kΩ resistor → NPN base; emitter → GND; collector → buzzer `-`; buzzer `+` → 5 V (common GND) |
| Ground rail | GND | 6 | Pi GND → breadboard ground rail |

> Power off the Pi before changing wires. The Freenove RFID starter kit guide uses an S8050 NPN transistor to drive its buzzer from 5 V: the Pi GPIO **must not** connect directly to 5 V or directly supply the kit buzzer. Check the transistor's pinout before wiring. For other buzzers, verify voltage and current ratings first.

Set `Gpio:BuzzerMode` to `Passive` (default) for the kit's passive buzzer; it uses software-generated ~2 kHz square waves. Set it to `Active` for the kit's active buzzer, which sounds with a steady signal. If the buzzer wiring is unverified, set `Gpio__BuzzerEnabled=false` while testing LEDs; it will not open the buzzer GPIO. Software cannot increase the supply voltage or overcome an underpowered/miswired buzzer. On success, the blue LED turns on for 1.5 seconds. On validation, duplicate-email, or database failure, the red LED turns on while the buzzer beeps three times. All outputs return low afterward.

## MongoDB Atlas setup

1. In Atlas, allow the Raspberry Pi's public IP under **Network Access**.
2. Give the database user `readWrite` access to `smart_store`.
3. Copy `.env.example` to `.env` and place the Atlas URI in it:

   ```dotenv
   MONGODB_URI="mongodb+srv://USERNAME:PASSWORD@CLUSTER.mongodb.net"
   ```

The repository's local `.env` is ignored by Git. Both services load configuration in this order: process environment, the file named by `MONGODB_ENV_FILE` (if set), repository `.env`, then `$XDG_CONFIG_HOME/northstar/credentials.env` (default `$HOME/.config/northstar/credentials.env`). Earlier values win. No particular username or checkout location is required. Credentials stay outside the compiled app.

## Run on a development computer

Install the .NET 8 SDK, then run:

```bash
dotnet restore SmartStore.sln
dotnet test SmartStore.sln
dotnet run --project src/SmartStore
```

Open `http://localhost:5198`. GPIO automatically switches to log-only simulation when the application is not running on Linux.

Check MongoDB connectivity without exposing credentials:

```bash
curl http://localhost:5198/health
```

## Run on Raspberry Pi 5

On 64-bit Raspberry Pi OS, install the libgpiod version provided by your OS (`libgpiod3` on Debian 13/Trixie; `libgpiod2` on older releases):

```bash
sudo apt update
sudo apt install -y libgpiod3 # use libgpiod2 on older Raspberry Pi OS
sudo usermod -aG gpio "$USER"
# Sign out/reboot after changing group membership.
```

On Pi 5 with `libgpiod3`, the application selects the RP1 chip using the libgpiod v2 driver. GPIO signals occur only after a customer registration or demo checkout succeeds or fails; the LEDs are otherwise off.

Install the .NET 8 SDK/runtime on the Pi, copy the project and its untracked `.env`, then:

```bash
ASPNETCORE_URLS=http://0.0.0.0:5198 dotnet run --project src/SmartStore
```

From another device on the same network, open `http://PI_IP_ADDRESS:5198`. If GPIO cannot be opened, verify the wiring, `/dev/gpiochip*` permissions, and that the process user belongs to the `gpio` group.

To produce a self-contained deployment on the development computer:

```bash
dotnet publish src/SmartStore/SmartStore.csproj \
  -c Release -r linux-arm64 --self-contained true -o publish/pi
```

Copy `publish/pi` and an untracked `.env` to the Pi, then start `./SmartStore` with `ASPNETCORE_URLS=http://0.0.0.0:5198`.

## Self-checkout classroom demo

Open `/Checkout` for the customer flow and `/Management` for inventory and sales. Enter or scan a sample code (1001 Apple, 1002 Bread, 1003 Milk, 1004 Chocolate bar), add items, and choose **Approve** or **Decline** for the simulated payment. No payment API or card details are used. Approved checkouts save a sale in MongoDB's `demo_sales` collection, reduce the in-memory stock, and flash the blue LED. Declines and checkout failures trigger the red LED and buzzer. The management page shows recent demo sales and lets you change stock for testing. Stock resets to the sample values when the app restarts; sales remain in MongoDB. This is a classroom simulation, not a production checkout or secured management interface.

## Demonstration checklist

1. Start the application and visit `/health`; confirm `healthy`.
2. Submit one valid customer; confirm the success notice, Atlas record, recent list entry, and blue LED.
3. Submit the same email again; confirm the error notice, red LED, and three buzzer alerts.
4. Submit an incomplete form; confirm field validation and the failure hardware signal.
5. Disconnect Atlas or remove network access, submit, and confirm graceful failure feedback.

## Group task description table

Complete this before submission.

| Student | Assigned tasks | Files/components | Verification |
|---|---|---|---|
| _Name_ | Project architecture and integration | `Program.cs`, configuration | Build and end-to-end demo |
| _Name_ | Customer UI and validation | `Pages/Index.*`, CSS/JS | Responsive and invalid-form tests |
| _Name_ | MongoDB data layer | `Data/`, `Models/` | Insert, duplicate, recent-query tests |
| _Name_ | Raspberry Pi circuit and GPIO | `Services/`, breadboard | Success and failure signal tests |
