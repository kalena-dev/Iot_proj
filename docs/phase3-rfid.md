# Phase 3 RFID: Pi reader, MacBook checkout

## Run the Atlas checkout on one Pi

From the repository root run `pi/start-checkout.sh`, then open
`http://127.0.0.1:5080/Checkout`. The launcher builds the current checkout and
starts the web and reader services. Set `MONGODB_ENV_FILE` to your private file
containing `MONGODB_URI` or use `~/.config/northstar/checkout-atlas.env`.
Local checkout pages pair automatically. Enter a registered EPC or UPC directly.
For an unfamiliar barcode, choose an inventory product and select **Add item**.
This manual association lasts for the current cart. For automatic hardware scans,
register the EPC on the product or configure an alias to its existing inventory UPC.

An approved payment displays its itemized receipt. **Management → Recent sales**
includes guest orders and links to their receipts. **Purchase history** searches by
registered customer email.

## Vendor CLI on the Pi

The ARM64 SDK's `CDemo.out` successfully inventoried the classroom CF600 using
its **COM** menu option on `/dev/ttyUSB0` at 115200. The USB cable stays connected.
The checkout bridge drives that menu, reads each complete `Data:` record, and
posts each new EPC to the paired checkout while the inventory is running.
The app resolves product codes and deduplicates across inventory cycles.

1. On the Pi, from `~/Iot_proj`, check the device and run one inventory:

   ```sh
   lsusb -d 0483:5750
   ls -l /dev/ttyUSB0
   # If the reader is listed but the serial port is missing:
   # sudo modprobe usbserial vendor=0x0483 product=0x5750
   python3 pi/cf600_sdk.py
   ```

   The adapter uses the SDK in `~/Downloads/UHF Desk Reader SDK/Sample/CDemo/Source/ARM64`.
   Use `--sdk-dir` or `--port` if needed. The process runs as your login user
   with serial-port access. Close any other reader program first. Each printed
   JSON array contains an EPC. The CLI performs 100 reads per cycle and can
   take several minutes without tags. It prints only the first 12 bytes of an
   EPC. For longer tags use `cf600_reader.py` as described below.

2. On the SmartStore host, set Reader mode, a private key, and a product
   mapping for each physical tag in range. Step 1 below shows the server
   command. For example, this shell-only mapping assigns a tag observed on
   the Pi to UPC 1001, if that UPC exists in the inventory:

   ```sh
   export Rfid__EpcProducts__E2801191A503006599BFCCFD=1001
   ```

3. Open `/Checkout` and keep that page open. On the Pi set the three bridge
   environment variables as in step 4 below. For checkout running on the Pi
   at port 5080, use `RFID_SERVER_URL=http://127.0.0.1:5080`. For checkout
   on the Mac use the SSH tunnel below. Then run:

   ```sh
   cd ~/Iot_proj
   python3 pi/rfid_bridge.py --sdk
   ```

   If you deploy the bridge separately, copy `pi/cf600_sdk.py` alongside
   `pi/rfid_bridge.py`. The bridge repeats inventory until Ctrl-C or a
   reader/delivery error. For HTTP 400 check the product mapping for each EPC
   from step 1. After checkout, pair the bridge to the new page.

For a Windows fallback, use the vendor GUI to inventory and copy a complete
EPC. With Python installed, the existing bridge also accepts JSON on stdin.
In PowerShell set `$env:RFID_SERVER_URL`, `$env:RFID_API_KEY` and
`$env:RFID_SESSION` to the paired checkout values, then from the repo run
`'["E2801191A503006599BFCCFD"]' | py -3 pi/rfid_bridge.py` with the actual
mapped EPC from the GUI. This is a manual transfer. Use HTTPS for a remote
checkout or an SSH tunnel to loopback.

The raw serial adapter below remains available as a second reader option. It
parses the classroom protocol and sends JSON arrays to the same bridge.

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

Use an existing UPC from **Products** as the value of any optional EPC alias.
Registered product EPCs take precedence. Reopen checkout after changing inventory
or restarting the app.

### 2. MacBook terminal B: copy the reader files to the Pi if needed

Skip the copy if the Pi already has the scripts. For the raw serial option:

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
under **Pair reader manually**. Keep that page open. Each reload creates
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
2. Verify one mapped tag adds one unit of its product and stays at one while held there.
3. Verify a second mapped physical tag adds its own item; retry inventory without
   increasing either tag's quantity.
4. Verify manual entry/removal and insufficient-stock behavior still work.
5. With your working MongoDB connection, test decline (declined attempt recorded) then approve
   (sale saved, stock reduced) using the existing demo payment controls.
6. After checkout/reload, stop the pipeline, copy the new pairing session, and
   restart. For the next customer, remove the previous customer's tags first.

Repeat these checks when changing the reader, mapping or deployment.

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
python3 -m unittest discover -s pi -p 'test_cf600*.py'
python3 -m unittest discover -s pi -p 'test_rfid_bridge.py'
```

Python parser tests cover fragmented frames, multiple tags, duplicates, invalid
length/status, exact inventory bytes, and no-response behavior. They need neither
pyserial nor hardware. Browser tests cover stock limits and duplicate/removal
behavior; .NET tests cover mapping, authentication, session isolation/expiry,
and event endpoint rejection paths. None of those establishes a live serial
scan, tunnel delivery, or a MongoDB sale against your class deployment.
