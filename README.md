# Northstar Smart Store — Phase 1

A C#/.NET 8 IoT application for the Vanier College **420-521-VA** final project. It captures customer information in a Razor Pages web form, saves it to MongoDB Atlas, and signals the result through Raspberry Pi 5 GPIO outputs.

## Architecture decisions

| Layer | Choice | Responsibility |
|---|---|---|
| Presentation | ASP.NET Core Razor Pages | Responsive customer form, validation, notifications, recent records |
| Application | C# / .NET 8 | Request handling, normalization, error handling, dependency injection |
| Data communication | MongoDB .NET Driver over TLS | Sends customer documents to MongoDB Atlas |
| Database | MongoDB Atlas | `smart_store.customers`, with a unique email index |
| Device output | `System.Device.Gpio` | Blue success LED; red failure LED and active buzzer |

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

The configured numbers are **BCM GPIO numbers**, not header positions.

| Component | GPIO | Physical pin | Connection |
|---|---:|---:|---|
| Blue LED | BCM 17 | 11 | GPIO → 220–330 Ω resistor → LED anode; LED cathode → GND rail |
| Red LED | BCM 27 | 13 | GPIO → 220–330 Ω resistor → LED anode; LED cathode → GND rail |
| Active 3.3 V buzzer | BCM 22 | 15 | Buzzer `+` → GPIO; buzzer `-` → GND rail |
| Ground rail | GND | 6 | Pi GND → breadboard ground rail |

> Turn the Pi off before changing wires. Use only a low-current 3.3 V active buzzer that stays within the GPIO current limit. A 5 V or higher-current buzzer needs a transistor driver and must not be connected directly to a GPIO pin.

On success, the blue LED turns on for 1.5 seconds. On validation, duplicate-email, or database failure, the red LED turns on while the buzzer sounds three times. All outputs return low afterward.

## MongoDB Atlas setup

1. In Atlas, allow the Raspberry Pi's public IP under **Network Access**.
2. Give the database user `readWrite` access to `smart_store`.
3. Copy `.env.example` to `.env` and place the Atlas URI in it:

   ```dotenv
   MONGODB_URI="mongodb+srv://USERNAME:PASSWORD@CLUSTER.mongodb.net"
   ```

The repository's local `.env` is ignored by Git. Alternatively, leave credentials outside the repository and set `MONGODB_ENV_FILE` to that file's absolute path. Real environment variables take precedence over `.env` values.

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

On 64-bit Raspberry Pi OS:

```bash
sudo apt update
sudo apt install -y libgpiod2
sudo usermod -aG gpio "$USER"
# Sign out/reboot after changing group membership.
```

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
