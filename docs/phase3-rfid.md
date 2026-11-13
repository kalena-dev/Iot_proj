# Phase 3 RFID: Pi reader, MacBook checkout

## Existing integration and this change

The current `fahad-phase3-checkout` branch already contains these uncommitted
components, which this serial integration reuses:

- `Services/RfidCheckout.cs`: `Rfid:EpcProducts` maps EPCs to existing product
  codes; `/api/rfid/events` accepts authenticated EPC batches for a paired session.
- `Pages/Checkout.cshtml.cs`: `OnGetRfid` supplies tags to that checkout page.
- `wwwroot/js/checkout.js`: `pollReader` calls `processRfidScan`, which adds the
  mapped products through the existing cart and stock logic and remembers EPCs.
- `pi/rfid_bridge.py`: JSON lines on stdin -> authenticated HTTP events.

The missing reader is now `pi/cf600_reader.py`. It uses the classroom PDF's CF
frame parser and the settings demonstrated by the user on a Raspberry Pi 5:
`/dev/ttyUSB0`, 115200 baud, inventory bytes `04 FF 01 1B B4`, 0.5-second polling,
serial timeout 0.5 seconds. It outputs JSON EPC arrays, never sample readings.
The prior CFApi HID SDK timeouts are not used by this serial path. No vendor SDK,
RF setting changes, firmware changes, or teammates' backend rewrites are needed.

The user reported a real EPC read in class. The serial script and live checkout
integration still need verification on that hardware. Like the provided demo,
the parser checks frame length/status/EPC length but does not verify response CRC;
the setup guide does not specify its algorithm. Tests use synthetic frames, not
captured hardware responses.

## Run in order

Replace `your-pi-user@your-pi-host` below with the SSH login you already use for
the Pi (for example a username plus its IP address). Do not use the Mac's serial
port: the reader remains physically connected to the Pi.

### 1. MacBook terminal A: configure and start checkout

From zsh, run:

```sh
cd /Users/macbook/Documents/GitHub/Iot_proj
git branch --show-current
export Rfid__Mode=Reader
export Rfid__ApiKey="$(openssl rand -hex 32)"
export Rfid__EpcProducts__E2801191A503006599BFD15D=1001
export Gpio__Enabled=false
export Security__UseHttpsRedirection=false
export ASPNETCORE_ENVIRONMENT=Development
printf 'Copy this temporary key to the Pi prompt in step 4: %s\n' "$Rfid__ApiKey"
dotnet run --project src/SmartStore --no-launch-profile --urls http://127.0.0.1:5080
```

Keep this terminal running. The existing private `.env` MongoDB settings are loaded
by `DotEnvLoader`; retain your working `MONGODB_URI`. Do not replace it or paste it
into chat. GPIO is disabled on the Mac; this does not remotely drive the Pi LEDs.

The `Rfid__EpcProducts__...=1001` line is the **optional test association** you
requested: your physically read tag maps to the existing Apple product (code
1001 in `StoreDemo`). It configures the existing lookup in this terminal only;
it does not hardcode the EPC in application source or create a new product.
Change `1001` to another existing code (1002 Bread, 1003 Milk, 1004 Chocolate bar)
if desired. Add another mapping variable for every other physical EPC. Restart
the app after changing mappings. Do not give an EPC a product code in Python.

### 2. MacBook terminal B: copy the actual uncommitted reader files to the Pi

A `git pull` on the Pi cannot retrieve uncommitted files. Copy these two scripts:

```sh
cd /Users/macbook/Documents/GitHub/Iot_proj
ssh your-pi-user@your-pi-host 'mkdir -p ~/phase3-rfid'
scp pi/cf600_reader.py pi/rfid_bridge.py your-pi-user@your-pi-host:phase3-rfid/
```

Then open an SSH shell with a reverse tunnel:

```sh
ssh -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -R 127.0.0.1:15080:127.0.0.1:5080 your-pi-user@your-pi-host
```

Keep this connection open. Commands typed in it now execute on the Pi. The Pi's
`http://127.0.0.1:15080` reaches the Mac's checkout through encrypted SSH; plain
HTTP is confined to loopback on each host. No Mac Remote Login service or LAN
HTTP listener is required. If the reverse forwarding port is occupied, close
an older tunnel first; do not continue after a forwarding failure.

### 3. MacBook browser: obtain the pairing session

Open `http://127.0.0.1:5080/Checkout`. Confirm **Reader mode** and copy the value
under **Pair this checkout with the Pi**. Keep that page open. Each reload creates
a new session, and each session expires after 30 minutes. Treat both the pairing
value and API key as private credentials.

### 4. Pi shell from step 2: check the reader and run it

Stop the teacher's demo and any other program using the serial port first.
The Pi already has `python3-serial`; use its system Python, without a virtualenv:

```sh
cd ~/phase3-rfid
/usr/bin/python3 -c 'import serial; print(serial.__version__)'
ls -l /dev/ttyUSB0
```

If `/dev/ttyUSB0` disappeared after reboot, the setup guide's driver command is:

```sh
sudo modprobe usbserial vendor=0x0483 product=0x5750
ls -l /dev/ttyUSB0
```

Run it only if the device is missing. Do not change the working baud rate or RF
settings. If opening the port is denied, check `id -nG` and the device group;
for the usual `dialout` group use `sudo usermod -aG dialout "$USER"`, then log out,
reconnect with the tunnel command, and repeat these steps. Do not make the device
world-writable. Only if `import serial` fails, install `python3-serial` via apt.

In the Pi's bash shell:

```sh
export RFID_SERVER_URL=http://127.0.0.1:15080
read -r -s -p 'Paste API key from Mac terminal A: ' RFID_API_KEY; echo
export RFID_API_KEY
read -r -s -p 'Paste pairing session from checkout page: ' RFID_SESSION; echo
export RFID_SESSION
set -o pipefail
/usr/bin/python3 -u cf600_reader.py | /usr/bin/python3 -u rfid_bridge.py
```

The reader's status goes to stderr; only EPC JSON batches go through the pipe.
Hold the mapped tag near the reader. Its product should appear in checkout in
about 1–2 seconds. Holding it there or removing and presenting it again must not
increase quantity in the same session. Different physical EPCs mapped to the
same product add separate units, subject to stock. Removing an RFID cart item
keeps its EPC suppressed for that session, so continuous inventory cannot re-add
it. Stop the pipeline with Ctrl-C before refreshing/re-pairing.

For reader-only diagnosis, stop the pipeline and run
`/usr/bin/python3 -u cf600_reader.py`; actual EPC JSON should appear in the Pi
terminal when a tag is present. Ctrl-C, then restart the pipeline. If reader-only
output works but the bridge rejects delivery, check the tunnel, API key, session
and mapping. Unknown tags reject the entire batch (400), so map every tag near
the reader or remove unmapped tags from the field. A reload/expired pairing is
404; wrong key or Simulation mode is 401. A stopped bridge requires restarting
the pipeline. No-response inventories produce no fabricated EPC or fallback data.

### 5. Live acceptance in class

1. Verify the reader-only script reports your actual tag, then use the pipeline.
2. Verify one mapped tag adds one Apple and stays at one while held there.
3. Verify a second mapped physical tag adds its own item; retry inventory without
   increasing either tag's quantity.
4. Verify manual entry/removal and insufficient-stock behavior still work.
5. With your working MongoDB connection, test decline (no sale) then approve
   (sale saved, stock reduced) using the existing demo payment controls.
6. After checkout/reload, stop the pipeline, copy the new pairing session, and
   restart. For the next customer, remove the previous customer's tags first.

These are live checks to perform, not claims of tests already run locally.

## Modes, security and limits

Default `Rfid:Mode=Simulation` retains the labelled simulation button and its
sample tags. To switch back from the shell above, stop the server, set
`export Rfid__Mode=Simulation`, and restart it. Reader events are rejected in
simulation. Manual checkout, server prices/stock checks and MongoDB payment
handling remain unchanged.

`POST /api/rfid/events` requires `X-Rfid-Key` (at least 32 characters configured
server-side), and JSON `{ "session": "...", "epcs": ["..."] }`. Product codes
are resolved on the server; neither prices nor mappings come from the reader.
Batches allow 1–100 hexadecimal EPCs and at most 16 KiB. Invalid/missing mappings
return 400 atomically; invalid sessions 404; wrong keys 401; large bodies 413.
Sessions hold at most 100 tags, with at most 100 live sessions. Retries are
idempotent. Browser polling uses a private capability header and uncached replies.
Sessions are in memory; use a single app instance, and re-pair after restart.
This is the existing classroom checkout, not a production payment system.

## Local verification commands

```sh
dotnet build SmartStore.sln --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet test SmartStore.sln --no-build --no-restore
node --test tests/checkout/checkout.test.cjs
python3 -m unittest discover -s pi -p 'test_cf600_reader.py'
python3 -m unittest discover -s pi -p 'test_rfid_bridge.py'
```

Python parser tests cover fragmented frames, multiple tags, duplicates, invalid
length/status, exact inventory bytes, and no-response behavior. They need neither
pyserial nor hardware. Browser tests cover stock limits and duplicate/removal
behavior; .NET tests cover mapping, authentication, session isolation/expiry,
and event endpoint rejection paths. None of those establishes a live serial
scan, tunnel delivery, or a MongoDB sale against your class deployment.
