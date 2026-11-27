# Email setup — shared STARTTLS transport

The web app sends account registration, staff verification, recovery and receipt mail. The refrigeration service sends temperature alerts and reads YES/NO replies. **Use SMTP587 with STARTTLS for both.** The C# sender does not support implicit TLS465; the guided Gmail default is587.

## Private setup

Use a dedicated application mailbox. Sign in yourself, enable the provider's required two-step verification and create an application password. For Gmail: <https://myaccount.google.com/apppasswords>. Do not substitute the main sign-in password or paste secrets into chat, source, browser URLs or command arguments.

Configure SMTP host/587/user/password/from, IMAP host/993/user/password, the alert recipient and `EMAIL_ENABLED=true` in the appropriate private role files. Deployed web and IoT environments are **separate**; updating one must not grant the fan mailbox/database access.

For local development with one private configuration file:

```bash
npm --prefix iot-server run setup:email
```

For an explicitly selected IoT role file:

```bash
NORTHSTAR_CONFIG_FILE=/private/path/iot.env npm --prefix iot-server run setup:email
```

Run as an authorized operator with file permission; do not loosen deployed `root:role 0640` ownership to make the wizard writable. Copy only the web-required SMTP settings into its private environment through the normal administrator procedure, preserve each role's other settings, restore permissions, and restart the relevant service.

The wizard masks the password, verifies SMTP and IMAP, submits one clearly labelled setup message, and atomically saves successful settings. It respects authoritative `NORTHSTAR_CONFIG_FILE`, then explicit `MONGODB_ENV_FILE`, otherwise project `.env`. Process environment overrides are reported. It never starts a motor. SMTP acceptance is not proof of final delivery: check the actual inbox/spam folder.

For a published installation, use `node iot-server/dist/setup-email.js`. The separate single-till Pi helper `python3 pi/setup-checkout-gmail.py sender@gmail.com` also uses587 STARTTLS; it is not the hardened service installer.

## Accounts and receipts

Customers register name/email/password, then enter the code from their inbox. Staff use an additionally provisioned immutable account ID and a fresh emailed sign-in code. Recovery revokes old session stamps. Registration/recovery responses remain generic and mail limits apply to recipients as well as the network boundary.

Checkout accepts nonmembers. Signed-in membership earns loyalty; a guest email, where provided, is a receipt destination, **not** ownership of an account or historical balance. A receipt-send failure does not undo a committed purchase. My account shows only that immutable account's purchases/points, never every receipt sharing its email.

Receipts contain date/time, item quantities/unit prices, total and points. `sent` in the log means SMTP accepted the message. Pending/failed outcomes must not be described as inbox delivery. Do not replay a purchase to retry email.

## Required refrigerator approval

1. Keep both sensors publishing fresh readings.
2. Save a threshold below the measured temperature.
3. Confirm the real alert states the current temperature and asks whether to turn on the shared fan.
4. From the configured recipient, reply **NO** on the first nonblank line with the subject unchanged; the fan must not start.
5. For a fresh eligible alert, reply **YES**. It authorizes one durably associated command. Show pending until the Pi confirms GPIO output; inspect actual motor movement separately.
6. Use OFF to preempt an outstanding ON. Keep the independent physical cutoff available.

Opening the incoming reply in the application's mailbox does not make it ineligible. Processing uses durable mailbox UID/UIDVALIDITY progress, isolates malformed messages and does not starve older eligible replies behind newer mail. Restart must not turn one consumed approval into a fresh ON command.

The default reply expiry and per-fridge cooldown are15 minutes. Sending retries no faster than once a minute while fresh/hot, and uses the latest temperature/threshold. Sensor/control work is not held behind SMTP delays. Recovery or stale telemetry does not authorize the motor.

**Residual risk:** From headers plus a private subject token are not cryptographic sender authentication. This required classroom email workflow relies on the protected provider/mailbox. Tokens are redacted from stored logs; do not forward live approval messages or share the application mailbox.

## Diagnostics

```bash
# Read-only: no mail submitted or flags changed.
npm --prefix iot-server run check:email
# Published equivalent:
node iot-server/dist/setup-email.js --check

# After an authorized configuration change:
sudo systemctl restart northstar-iot northstar-web
```

The dashboard reports mailbox connectivity, not proof that each outgoing message arrived. Safe diagnostics omit raw addresses, passwords, AUTH payloads and server responses. Account/receipt/control tests should use synthetic identities and an isolated local sink, not a real customer mailbox.

OAuth-only providers need an approved OAuth integration; a regular password is not a workaround. Provider approval, actual delivery, Pi wiring, motor rotation and venue networking require live access and cannot be established by unit tests.

## Record lifecycle

Staff access to mail/transaction logs is authorized access, not a reason to retain records forever. Use synthetic classroom data whenever possible. After grading and the agreed review period, the group owner must authorize removal of synthetic personal records and expired mail/challenge data; retain only required receipts/inventory history and security evidence for the agreed period. Do not run deletion against real records without the owner's explicit approval.

Keep database backups encrypted, access-controlled and subject to the same retention period. Store CA keys, mailbox app passwords, backup keys and database administration credentials outside application accounts. On loss/compromise, revoke the mailbox password, rotate role credentials/keys, disable affected accounts/global sessions, isolate devices and inspect attributed events.

Before treating backups as recovery: restore into an isolated, differently named replica-set database with a separate restore identity; compare receipt totals/points/stock and per-unit states; verify indexes and account revocation; record expected/actual results and cleanup. Never test restore over the live store. No backup/restore of a real deployment is implied by repository checks.

See [PHASE2.md](PHASE2.md) and [README](../README.md) for separate users, TLS/ACLs, wiring and final demonstration requirements.
