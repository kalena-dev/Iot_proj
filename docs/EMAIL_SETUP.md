# Email setup: minimal manual steps

The application uses SMTP for alerts and IMAP for YES/NO replies. **Gmail is the guided default**; the normal `.env` settings still support other providers that permit SMTP/IMAP password authentication.

## What you must do once

1. Choose a Gmail application mailbox (a dedicated account is recommended). Sign in yourself, enable **2-Step Verification**, then create an app password at **https://myaccount.google.com/apppasswords**. Name it something like `Northstar`. Google login, account creation and two-factor approvals cannot be automated by this application. If App passwords is unavailable, check account restrictions; a school/work administrator may prohibit it.
2. Run the setup command below and enter the Gmail address, your **alert recipient/approval address**, and Google's **16-character app password**. Use a different recipient mailbox so replies arrive as new incoming messages. The recipient can be an existing school/personal email; it does not need SMTP/IMAP credentials in this application. Paste secrets only into the masked local prompt, not chat or command arguments.
3. Confirm the setup test arrived (check spam/quarantine). Reply **NO** to a subsequent real temperature alert, keeping its subject, to verify the complete reply path without starting a motor.

```bash
npm --prefix iot-server run setup:email
```

For a built installation without development dependencies:

```bash
node iot-server/dist/setup-email.js
```

## What setup automates

- Fills Gmail SMTP `smtp.gmail.com:465` and IMAP `imap.gmail.com:993`, using TLS.
- Uses the Gmail mailbox for SMTP sender, IMAP access and Reply-To.
- Removes spaces from the app password and never echoes/logs it.
- Verifies SMTP authentication **and** IMAP INBOX access before applying settings.
- Sends one clearly labelled setup test to the address you approve. This test cannot authorize any motor action.
- Saves all email settings with `EMAIL_ENABLED=true`, atomically and with file permissions `600`. MongoDB, MQTT and API settings are preserved.
- Writes to `MONGODB_ENV_FILE` if explicitly set, otherwise the repository's private `.env`. Warns if real process environment variables would override the new values.
- Restarts this checkout's running `northstar-iot` systemd service when noninteractive sudo is permitted, then checks live reply polling. It never restarts an unrelated checkout. If restart is not permitted or the service is not installed, it prints the manual restart instruction.

No passwords are written to source files, shell history, browser URLs or the ESP32 firmware. **No ESP32 reflash is needed for email changes.** Gmail mailbox access is sensitive: protect the private configuration file and revoke the app password if the machine is lost.

If authentication or test submission fails, setup does not modify the configuration. It retains your entries in memory and offers retry, a fresh app password, corrected mailbox/recipient, or an SMTP port change (465 TLS / 587 required STARTTLS). Exiting discards those unsaved entries. Retries may submit another labelled test email. SMTP acceptance is not a guarantee of final external delivery: checking the recipient's inbox is still necessary.

Failures show a safe stage (`smtp-sign-in`, `imap-inbox` or `smtp-submit`), allowlisted error/response codes and guidance. A private report is saved to `$XDG_STATE_HOME/northstar/email-setup-diagnostic.json` (default `$HOME/.local/state/northstar/email-setup-diagnostic.json`). It contains no raw server responses, addresses, AUTH payloads or passwords. Its timestamp refers to the latest failed attempt; it is not proof the current configuration is still failing.

## Diagnostics and restart

```bash
# Read-only: check SMTP/IMAP access; sends nothing and does not enable email.
npm --prefix iot-server run check:email

# Built equivalent:
node iot-server/dist/setup-email.js --check

# Managed service restart/logs:
sudo systemctl restart northstar-iot
journalctl -u northstar-iot -n 40 --no-pager
```

The guided diagnostic also works with manually configured non-Gmail providers. OAuth-only providers require an OAuth implementation; a regular sign-in password is not a substitute for a provider-approved app password.

The dashboard shows **reply-mailbox connectivity**, not proof that every outgoing email arrived. Each fridge has a 15-minute default cooldown; failed sends retry at most once per minute while readings remain fresh and hot. Both fridges share one fan. Changing the recipient invalidates old approvals and prevents queued messages from being sent to the previous/placeholder address.

## What remains physical/manual

- Sensors must stay powered and online; these classic ESP32s require 2.4 GHz WiFi.
- Verify the motor-driver wiring and power supply before enabling `northstar-fan`, then set `FAN_WIRING_CONFIRMED=true`. For the L293D mapping, use `FAN_GPIO=27` (EN1), `FAN_IN2_GPIO=17`, IN1 tied LOW, and `GPIO__ENABLED=false` to disable the conflicting old status GPIO. Setup intentionally does not enable physical GPIO/motor control.
- After wiring is verified, start the real controller and test YES using a fresh real alert. Reply from the exact alert-recipient address, keep the subject/token and put **YES** alone on the first line. NO never starts the fan; use dashboard OFF or the safety timeout to stop an already-running fan.

See [PHASE2.md](PHASE2.md) for wiring, safety and service installation. Do not mistake the isolated simulated integration test for proof of a wired motor or external email delivery.
