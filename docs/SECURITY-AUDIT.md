# Northstar Smart Store — Security Audit

> **Historical baseline:** F-01–F-20 below describe the pre-remediation checkout. Source line references and the original verdict are retained as audit evidence, not a statement that every finding remains present. See the remediation record at the end for current controls and verification limits. No real deployment is certified by this document.

**Scope:** read-only review of the checked-out `main` source (`fb8353f`), deployment examples, and existing tests for Phases 1–3. The review covered the ASP.NET web app, customer and staff accounts, MongoDB, checkout and RFID, the TypeScript refrigeration service, Mosquitto, ESP32 firmware, the Pi fan controller, email, and the Minew sensor dashboard. It used the course deck `IoT-Security-01.pptx` and relevant publications from [Samad Rostampour's publication list](https://www.samadrostampour.com/publications).

**Verdict:** fix the documented LAN web and MQTT configurations before using the project on a shared network with real customer data or an enabled physical fan. Until then, keep the web app on loopback or behind trusted HTTPS, isolate the IoT network, and use synthetic records and inventory. Leave the physical fan disabled unless network isolation and the independent local cutoff are verified. The [single-till RFID launcher](../pi/start-checkout.sh#L10-L19) already binds its web traffic to loopback; it is not the all-interface LAN deployment.

**Evidence limits:** source and configuration examples establish the behaviors below, not what is actually deployed. No app, test suite, scanner, or network probe was run for the audit. The Pi, physical wiring, Atlas tenant, private credentials, Git history, and complete advisory coverage were not verified. Severity assumes the documented classroom deployment and is not a CVSS score. Suggested acceptance checks are for an isolated environment with synthetic records and simulated GPIO unless explicitly noted.

## High — fix before shared-network use

### F-01 — The documented LAN web listener allows plaintext credentials and sessions

The [Pi service](../pi/systemd/northstar-web.service#L9-L12) binds to all interfaces on HTTP. [HTTPS redirection is disabled](../src/SmartStore/appsettings.json#L24-L26), and [the authentication cookie](../src/SmartStore/Program.cs#L26-L35) is Secure only when the request is already HTTPS. If the documented listener is reachable, a party able to observe traffic can see passwords or session cookies; HSTS does not protect a first HTTP visit.

**Fix and verify:** expose only trusted HTTPS, keep any plaintext backend listener private, and require Secure auth cookies. Verify the effective scheme behind a trusted proxy, that HTTP does not serve account forms, and that HTTPS login sets a Secure cookie.

### F-02 — ESP32 MQTT transport does not authenticate the broker or encrypt traffic

The [Mosquitto example](../pi/mosquitto.conf.example#L1-L6) uses plaintext MQTT. The [ESP32 implementation](../firmware/FridgeSensor/FridgeSensor.ino#L19-L66) uses a plain network client and accepts a discovered broker without verifying its identity before [sending MQTT credentials](../firmware/FridgeSensor/FridgeSensor.ino#L110-L123). Securing just the Node and Python clients does not secure the ESP32 link.

**Fix and verify:** require certificate-validated TLS on all clients. Use discovery for location, not identity. Verify both ESP32s can connect to the expected broker and reject an untrusted certificate.

### F-03 — Shared MQTT credentials and missing topic ACLs blur device authority

The [setup guide](PHASE2.md#L84-L100) provisions one MQTT identity for the sensors and services. The [broker example](../pi/mosquitto.conf.example#L1-L6) disables anonymous access but defines no topic ACL. The [IoT service](../iot-server/src/main.ts#L55-L81) identifies telemetry and status by topic, and the [fan worker](../pi/fan_controller.py#L252-L279) accepts commands delivered on its command topic. On a reachable broker using that policy, loss of one sensor credential can compromise telemetry, reported fan state, and actuation, even if TLS is enabled.

**Fix and verify:** give each ESP32, the IoT service, and the fan separate revocable identities with deny-by-default topic permissions. Each ESP32 can publish only its own readings; the fan reads commands and publishes only its status; the IoT service reads telemetry/status and is the only command publisher. Verify each allowed and denied role in an isolated broker setup, then retire the old credential.

## Medium — authorization, privacy, integrity, and availability

### F-04 — Dashboard OFF cannot preempt pending ON; new ON commands extend the run

The [dashboard](../src/SmartStore/wwwroot/js/refrigerators.js#L34-L40) disables both buttons while a command is pending. The [server applies the same gate](../iot-server/src/monitor.ts#L137-L151) to OFF and ON. If ON reaches the Pi but confirmation is lost, OFF through the dashboard must wait. The [Pi run timer](../pi/fan_controller.py#L19-L28) uses wall time, and each distinct ON [resets the deadline](../pi/fan_controller.py#L67-L86). The dashboard is not an emergency stop.

**Fix and verify:** let OFF preempt pending ON, use a monotonic continuous-run limit on the Pi, and retain an independent physical cutoff. Verify that simulated OFF works without the previous acknowledgement and repeated ON cannot extend continuous operation.

### F-05 — An email reply is treated as permission to actuate the fan

The [mail handler](../iot-server/src/email.ts#L82-L95) passes a parsed `From` address to the [approval handler](../iot-server/src/monitor.ts#L152-L174). It checks the configured address, alert token, expiry, and fresh over-threshold telemetry; it does not independently verify sender authenticity. Provider TLS and filtering are separate controls. The [outbound log](../iot-server/src/outbound-email-log.ts#L12-L16) records the subject containing the token, which [staff can view](../src/SmartStore/Pages/Records.cshtml#L11-L18). This is not a public token leak or a separate staff privilege escalation. The decision is saved after ON is published, so a failure between those operations can leave an eligible approval available after restart.

**Fix and verify:** use email as a notification and require authenticated staff approval in the dashboard. If email replies are required by the assignment, document the residual risk, redact approval material from logs, and durably associate a single consumed approval with its command ID. Verify that one approval cannot actuate twice across restart.

### F-06 — Local service authorization and credential isolation are weak

The [Express API](../iot-server/src/main.ts#L84-L117) skips authentication if its key is empty. It is loopback-only, so this is a local trust-boundary gap, not a direct LAN endpoint. The [installer](../pi/install-services.sh#L32-L45) renders all three units for one OS user. The [shared Atlas configuration](../README.md#L86-L98) uses database-wide `readWrite`, and the [fan worker loads the shared configuration](../pi/fan_controller.py#L221-L229) despite needing no database or mail secret.

**Fix and verify:** fail startup on missing or example control keys, keep the API on loopback, separate service accounts and credential files, and use least-privilege database identities. Verify missing or incorrect credentials are rejected and the fan user cannot access Atlas or mail secrets.

### F-07 — A staff allowlist entry can elevate an existing account or session

[Staff authorization](../src/SmartStore/Services/StoreAccounts.cs#L33-L53) evaluates the signed-in email claim against configuration. It does not bind the grant to a separately provisioned identity or require fresh authentication. For example, if a mailbox is reassigned and then allowlisted, an old application account using that address can retain its old password.

**Fix and verify:** provision staff against immutable account IDs with explicit identity confirmation, MFA, and reauthentication on elevation. Verify that adding or reassigning a mailbox does not elevate a previously existing customer account.

### F-08 — Mailbox ownership can be mistaken for ownership of historical purchases

[Registration](../src/SmartStore/Pages/Register.cshtml.cs#L54-L82) proves current mailbox access. [Historical receipts and loyalty](../src/SmartStore/Services/StoreDemo.cs#L217-L247) are associated by email. A shared or reassigned mailbox may therefore inherit unclaimed records from a previous holder. Uniqueness prevents a second registration for an existing account; it does not protect unclaimed history.

**Fix and verify:** associate new purchases and balances with stable account IDs, and require separate proof to claim legacy data. Verify that a newly verified account cannot inherit another person's test records solely by email.

### F-09 — Logout does not revoke another copy of the session ticket

[Cookie authentication](../src/SmartStore/Program.cs#L26-L35) issues an eight-hour ticket without server-side revalidation. [Logout](../src/SmartStore/Pages/Logout.cshtml.cs#L9-L16) clears the current browser; it does not invalidate another copy. The repository has no account disable or recovery flow. Removing a staff email revokes that role once the new configuration takes effect, not the customer session itself.

**Fix and verify:** add account recovery, disable, and server-side revocation or security-stamp checks. Verify account disable or global sign-out ends all existing test sessions.

### F-10 — Account throttling is IP-only and registration mail lacks recipient quotas

[Login and registration](../src/SmartStore/Program.cs#L39-L46) share a fixed eight-per-minute POST budget per IP. That does not cap attempts against an account across multiple IPs, and unrelated users behind the same NAT can consume the same quota. [Registration sends mail](../src/SmartStore/Pages/Register.cshtml.cs#L40-L50) before the [duplicate account check](../src/SmartStore/Pages/Register.cshtml.cs#L72-L79).

**Fix and verify:** keep a network-level limit and add per-account failed-login pacing and recipient/global mail limits without revealing account existence. Verify limits with synthetic accounts and a local mail sink.

### F-11 — RFID pairing is shared and does not bind a reader session to a till

[Opening checkout](../src/SmartStore/Pages/Checkout.cshtml.cs#L53-L71) or a [receiving page](../src/SmartStore/Pages/Products/Receive.cshtml.cs#L42-L52) allocates a session in reader mode. The [RFID service](../src/SmartStore/Services/RfidCheckout.cs#L35-L69) has a shared 100-session cap, no per-user allocation limit, and a single local pairing file. Another local page can replace the bridge's active pairing; unused sessions occupy capacity until expiry.

**Fix and verify:** make pairing explicit and scoped to the till, authorized operator, and workflow. Reuse or close superseded sessions. Verify another tab cannot redirect an actively paired reader and that page reloads do not consume shared capacity indefinitely.

### F-12 — An EPC identifies an inventory record; it does not prove a scan or item authenticity

The [checkout path](../src/SmartStore/Pages/Checkout.cshtml.cs#L82-L94) accepts submitted codes independent of reader-event provenance. A [registered EPC lookup](../src/SmartStore/Data/ProductCodes.cs#L21-L35) binds a code to a stored item, while the [RFID bridge endpoint](../src/SmartStore/Services/RfidCheckout.cs#L115-L148) authenticates the bridge, not the physical tag. This is an assurance limit, not a claim that manual entry is a bug or that a physical tag was cloned. The [optional serial adapter](../pi/cf600_reader.py#L17-L44) also checks framing and length but not the response CRC; a CRC would detect accidental corruption, not authenticate a tag.

**Fix and verify:** keep manual entry available where required but distinguish it from reader-origin events. If authenticity becomes a requirement, select reviewed tag hardware and protocols that actually support it. Verify the system never reports a manually entered value as proof of a physical scan.

### F-13 — Checkout is transactional but not idempotent across HTTP submissions

The [checkout handler](../src/SmartStore/Pages/Checkout.cshtml.cs#L82-L94) accepts the user's simulated payment outcome; it does not confirm a real payment. A [sale ID is generated once per checkout invocation](../src/SmartStore/Services/StoreDemo.cs#L76-L104), not once per logical attempt across browser submissions. A repeat submission can create a second valid sale when the stock and coupon constraints permit it, even though each database transaction is atomic.

**Fix and verify:** isolate demo inventory and loyalty from real balances. Give each logical attempt an idempotency identifier enforced by the same transaction as stock and receipt writes. Verify a repeated attempt returns the original receipt without changing stock or loyalty twice.

### F-14 — The telemetry queue can delay controls and stamp delayed data as fresh

The [monitor](../iot-server/src/monitor.ts#L21-L66) uses one unbounded promise chain for telemetry and control actions. The [message limit](../iot-server/src/main.ts#L76-L81) bounds one payload, not rate or queued work. Readings are timestamped when queued processing starts, device sample IDs are [optional](../iot-server/src/model.ts#L5-L9), and [mail delivery](../iot-server/src/monitor.ts#L93-L106) also runs in that chain.

**Fix and verify:** bound per-device rate and queue depth, capture receipt time at ingress, require authenticated device identity and freshness rules, and separate safety commands and email work from telemetry. Verify delayed data becomes stale and control actions remain responsive under a bounded simulated load.

### F-15 — Mailbox processing can indefinitely skip older eligible replies

The [poller](../iot-server/src/email.ts#L82-L101) examines only the last 50 unread subject matches, and invalid matches remain unread. An older eligible reply can remain outside the window; one parsing exception aborts that polling pass. This matters only if email approval remains enabled.

**Fix and verify:** prefer authenticated dashboard approvals. If mail replies remain, use a durable mailbox cursor, isolate errors by message, and quarantine invalid matches. Verify newer unrelated messages cannot prevent processing an eligible older reply.

### F-16 — Known affected dependencies and incomplete audit coverage

The [loaded validator bundle](../src/SmartStore/wwwroot/lib/jquery-validation/dist/jquery.validate.min.js#L1-L3) is jQuery Validation **1.19.5**, affected by [CVE-2025-3573](https://github.com/advisories/GHSA-rrj2-ph5q-jxw2) (fixed in **1.20.0**). A reachable Northstar-specific input path was not established. [ImapFlow **1.7.8**](../iot-server/package-lock.json#L1976-L1984) is in the published affected ranges for [GHSA-q347-wc7v-rfx2](https://github.com/postalsys/imapflow/security/advisories/GHSA-q347-wc7v-rfx2) and [GHSA-g78f-3cr8-fhw5](https://github.com/postalsys/imapflow/security/advisories/GHSA-g78f-3cr8-fhw5); these require a hostile or compromised IMAP peer in this TLS configuration. The [checkout launcher](../pi/start-checkout.sh#L16-L20) disables NuGet auditing for its build. The [CF600 adapter](../pi/cf600_sdk.py#L26-L34) launches a native SDK from an externally provisioned directory; its installed binary and patch status were not verified.

**Fix and verify:** update or remove the validator, move ImapFlow to a supported patched release (**2.12.0** fixes the later advisory), restore automated advisory checks, and inventory Pi/Arduino packages, browser assets, and vendor binaries. Run the applicable integration checks after upgrades and inventory the deployed versions separately.

### F-17 — Inventory deletion removes traceability, and control logs lack actor attribution

[Deleting a product](../src/SmartStore/Data/MongoProductRepository.cs#L145-L155) also deletes its code mappings, EPC records, and receiving history. The checkout receipts remain, but the original per-unit records are lost. [Threshold updates and fan events](../iot-server/src/monitor.ts#L69-L75) do not provide a complete operator-attributed record of who changed what, [which command was sent](../iot-server/src/monitor.ts#L123-L150), and its outcome.

**Fix and verify:** archive products and retain required EPC history. Record actor or device identity, action, target, time, correlation ID, and outcome in access-controlled logs; do not include secrets or approval tokens. Verify a retired product and an operator's fan request remain traceable after the operation.

## Low — hardening and privacy

### F-18 — Customer email reaches diagnostics, and sensitive records lack a documented lifecycle

[Registry errors](../src/SmartStore/Pages/Index.cshtml.cs#L79-L83) log raw customer email. The [staff records page](../src/SmartStore/Pages/Records.cshtml#L11-L31) shows email recipients and transaction email addresses. This is authorized staff access, not public disclosure, but it increases the importance of access, retention, and redaction rules. There is a [30-day telemetry TTL](../iot-server/src/repository.ts#L7-L13), not a documented lifecycle for personal records, receipts, or mail logs.

**Fix and verify:** use operation IDs in diagnostic logs; define retention, access, and disposal rules for personal records and backups. Verify diagnostic output for a synthetic save failure contains no raw address.

### F-19 — Budget targets survive logout on a shared browser profile

[Spending JavaScript](../src/SmartStore/wwwroot/js/spending.js#L6-L29) writes budget targets to local storage under an account-derived key. [Logout](../src/SmartStore/Pages/Logout.cshtml.cs#L9-L16) does not clear that storage. Exposure is limited to someone with access to the same browser profile; this does not disclose purchase history.

**Fix and verify:** use authenticated storage or make local persistence an explicit private-device option. Verify the default shared-kiosk profile retains no budget target after logout.

### F-20 — The Minew dashboard can misclassify future or missing timestamps

The [Razor proxy](../src/SmartStore/Pages/Sensor.cshtml.cs#L19-L46) trusts a configured upstream and accepts any finite timestamp. The [browser freshness check](../src/SmartStore/wwwroot/js/sensor.js#L35-L59) has no upper bound on future times, and individual cards can appear normal or active without a valid observation time. This display does not actuate the fan.

**Fix and verify:** validate timestamp units and age bounds, mark missing times unknown, and require verified HTTPS if the upstream is remote. Verify missing, future, stale, and current observations receive the correct status.

**Additional availability hardening:** [purchase history](../src/SmartStore/Pages/PurchaseHistory.cshtml.cs#L34-L50) paginates after loading matching sales, and [staff report queries](../src/SmartStore/Services/StoreDemo.cs#L356-L365) materialize a full period and the distinct prior purchasers. Move paging and aggregation to MongoDB and bound report windows. No production impact or data volume was measured. The older [Phase 2 note calling the dashboard unauthenticated](PHASE2.md#L146-L152) is stale: the [current Razor policy](../src/SmartStore/Program.cs#L17-L24) makes it staff-only.

## Controls to preserve

- ASP.NET password hashing and rehash-on-login, expiring cryptographically random verification codes, Razor POST antiforgery, authenticated response `no-store`, and explicit staff/receipt ownership checks. No current Razor page authorization bypass was identified by static review.
- Server-side pricing and quantity checks, atomic stock/receipt/loyalty updates, and database transaction handling that distinguishes a failed purchase from an unknown commit outcome.
- RFID bridge key checks, uncached pairing responses, and EPC deduplication. The [bridge](../pi/rfid_bridge.py#L17-L31) requires HTTPS when not using loopback, and the [documented remote workflow](phase3-rfid.md#L111-L121) uses an encrypted SSH tunnel.
- Loopback binding for the IoT API, stale-reading indicators, non-retained fan commands, command expiry and duplicate checks, safe-OFF behavior on boot or disconnect, and explicit distinction between GPIO output confirmation and motor rotation.

## Course checklist

The course deck, slides **5–11** and **16–21**, provides general guidance, not a grading rubric. It covers risk, confidentiality, integrity, availability, responsibility, Identify/Protect/Detect/Respond/Recover, and how IT controls affect physical equipment.

| Area | Status | Evidence or next step |
| --- | --- | --- |
| Identify | Partial | The architecture is documented. Inventory physical devices, credentials, network boundaries, data classes, and control owners; write down risks accepted for the classroom. |
| Protect | Partial | Web access controls and transactional integrity are present. Resolve the documented transport and authorization gaps before broader use. |
| Detect | Partial | Connection status, stale readings, fan acknowledgements, and mail outcomes are present. Monitor authenticated source anomalies, actor-attributed control actions, and account abuse. |
| Respond | Partial | Safe-OFF behavior is present. Document who can cut power, disable accounts, revoke sessions, isolate a device, and rotate credentials. |
| Recover | Missing from repo; deployment not verified | Document a backup and restore drill, restore into an isolated Atlas-compatible environment, reconcile inventory against receipts, and record post-incident actions. |
| Physical and platform controls | Not verified | Inspect physical cutoff and access to the Pi and reader, effective network/firewall rules, Atlas access, and deployed OS, firmware, and broker patch status. |

## How the instructor's research informed this project

1. **[Rostampour et al., Securing Industrial IoT: A Novel Approach with MQTT Authentication (2025)](https://doi.org/10.1109/ICC52391.2025.11161120).** The published abstract addresses mutual authentication and message integrity. It informed F-02 and F-03: Northstar needs authenticated broker connections and a way to distinguish a sensor's authority from the service that can request actuation. Verified TLS and topic ACLs are practical controls for this prototype; Northstar does not implement the paper's protocol.
2. **[Bendavid, Hachani, and Rostampour, Designing and Developing an IoT-Enabled Smart Store Prototype for Small Enterprises (2022)](https://doi.org/10.3917/maorg.045.0049).** The prototype uses RFID for item identification and tracking. That directly motivates the distinction in F-12 between identifying a stored item record and authenticating a physical tag or proving it was scanned.
3. **[Mokhtari et al., Practical Security Analysis and Attack Strategies on Permutation Functions Used in IoT Supply Chain Systems (2025)](https://doi.org/10.1038/s41598-025-01041-y).** The paper shows that weaknesses in a lightweight primitive can undermine a protocol. It supports using established security controls instead of designing custom lightweight authentication. It does not show that Northstar implements or is vulnerable to the protocol studied.

**Recommended classroom demonstration:** show HTTPS-only login, show that each ESP32 can publish its own telemetry but not issue fan commands, then show that simulated OFF succeeds when an ON acknowledgement is missing. Record each expected and actual result; don't present these as already verified.

## Remediation record

The implementation preserves the course's simulated-payment and email-YES/NO requirements. It does not replace them with real payment processing, tag authentication or an unrelated production platform.

| Finding | Remediation / retained boundary |
|---|---|
| F-01 | HTTPS-only account surface; Always-Secure auth/antiforgery/TempData cookies; explicitly allowlisted trusted proxy; loopback backend template |
| F-02 | Certificate/hostname-validated MQTT TLS for firmware, Node and Python; offline CA/server provisioning; no insecure fallback |
| F-03 | Four separate MQTT identities and deny-by-default topic ACLs |
| F-04 | OFF can preempt ON; monotonic continuous-run cutoff; repeated ON cannot extend it; physical cutoff still mandatory |
| F-05 | Required email approval retained with durable approval/command identity and redacted tokens; provider/From-header trust is a documented residual risk |
| F-06 | Strong nonplaceholder local API key; authoritative per-role files and isolated service users; actual Atlas grants still require the tenant administrator |
| F-07 | Immutable explicitly provisioned staff IDs plus fresh password/email verification; existing customer sessions cannot elevate retroactively |
| F-08 | New purchases/balances/coupons belong to AccountId; legacy email-only data remains preserved and unclaimed |
| F-09 | Account recovery, disable/enable, global revocation and per-request security-stamp validation |
| F-10 | Persisted per-account password pacing and recipient/global mail quotas, with generic responses |
| F-11 | Explicit browser/till/workflow reader pairing; active ownership cannot be silently replaced; superseded allocations reclaimed |
| F-12 | Manual/simulated/reader-origin observations distinguished; no identifier is claimed as physical-tag authentication. Unknown vendor CRC remains an explicit limitation |
| F-13 | Owner-bound logical checkout identity enforced with the transactional receipt/stock/loyalty writes |
| F-14 | Bounded/coalesced sensor work, required sample identities, ingress timestamps and separate mail/control paths |
| F-15 | Durable mailbox UID/UIDVALIDITY progress and per-message isolation; read flags do not authorize or suppress processing |
| F-16 | Patched jQuery Validation, ImapFlow, Express/body-parser/qs, Nodemailer/mailparser and Zod; NuGet advisory suppression removed. Vendor/OS deployment inventory remains operator work |
| F-17 | Product retirement retains EPC/reception history; attributed control requests correlate actor, command and outcome without approval secrets |
| F-18 | Registration diagnostics omit raw customer addresses/exception payloads; private-data/backup retention and isolated restore procedure documented |
| F-19 | Budget targets are page-local, not persistent shared-browser data; legacy saved budget keys are removed |
| F-20 | Missing, non-millisecond, fractional and excessive future timestamps are unknown; remote Pareto requires HTTPS and redirects are not followed |

### Deployment and data responsibilities

Use [README](../README.md), [PHASE2](PHASE2.md), [email setup](EMAIL_SETUP.md) and [RFID setup](phase3-rfid.md), not the historical configuration examples described in the findings. Rotate old shared credentials; install the actual CA/certificate and hostname; explicitly provision staff; give each database service only its necessary collection privileges. The fan user must have no Atlas or mailbox credential access.

Use synthetic classroom records. The group owner must agree a retention period and authorize any deletion; no cleanup command is run against real records as part of remediation. Encrypt backups, protect restore keys separately, and prove restore only into a disposable non-production replica-set database. Reconcile receipts, loyalty, quantities and exact EPC states before accepting recovery.

On compromise: isolate the device/network; use the independent motor cutoff; disable affected accounts/revoke sessions; revoke mailbox passwords and rotate the affected MQTT/API/database credentials; inspect actor/command outcomes; recover from a verified isolated restore. Do not assume an application OFF button can stop a frozen OS or damaged driver.

### Verification boundary

Automated verification must use disposable loopback services, synthetic accounts/stock/mail and simulated GPIO. Actual deployment still requires Pi access, client certificate trust, Atlas administration, physical reader/sensor/wiring inspection, external mailbox delivery and an agreed group task allocation. Never infer those from a passing parser, mock or unit test.

### Observed isolated verification — 2026-11-27

Only disposable loopback databases/brokers, synthetic accounts/mail/inventory and simulated GPIO were used. No store database, real mailbox, reader, sensor or motor was accessed.

| Check | Observed result |
|---|---|
| .NET registry, identity, transactions, RFID, proxy, Minew and diagnostic-privacy regressions | 113 distinct cases passed across scoped runs using an isolated MongoDB replica set. A malformed account-ID test fixture was corrected; its complete 14-case RFID file passed on the final rerun |
| `node --test tests/checkout/checkout.test.cjs tests/ui/safety.test.cjs` | 17 passed |
| IoT TypeScript build and unit tests | Build passed; 50 tests passed |
| IoT integration tests | 2 passed: real TLS Mosquitto with simulated Python fan, and isolated MongoDB persistence/concurrency |
| Provisioned broker credentials/ACLs | Real Mosquitto accepted own-topic telemetry; rejected cross-sensor publication, sensor fan commands, anonymous/wrong-password connections and an incorrect certificate hostname |
| Python reader/fan tests | 37 methods passed. Two existing Linux SDK subprocess-fixture methods cannot execute on Windows, producing five `WinError 193` errors; this is not a physical SDK/reader verification |
| ESP32 firmware | Both Frig1/Frig2 configurations compiled with Arduino ESP32 3.3.3 using an external synthetic configuration; no board was flashed |
| Dependency advisories | `npm audit --omit=dev`: zero vulnerabilities; NuGet transitive vulnerability query: no vulnerable packages reported by configured sources |
| Shell scripts | Syntax passed for LF-normalized checkout content. `.gitattributes` pins shell scripts to LF; no user worktree/index was rewritten to normalize this existing Windows checkout |
| Actual Chromium UI and STARTTLS SMTP sink | Registration/name/membership, Secure/HttpOnly account cookie, explicit staff provisioning/fresh mail challenge, receiving, member and account-free guest checkout, emailed receipts, explicit reader pairing, queued manual EPCs/exact removal, UPC/price continuity, reports and page-local budgets exercised successfully |
| Live HTTP ownership boundaries | HTTP account request rejected with 400; guest receipt request without its owner cookie rejected with 404 |

The browser observed two purchases of the same product before/after its UPC and price changed: customer history retained both prices, and the administrator report kept one product row with two units. Manually entered EPCs were visibly labeled manual entry, not physical scan proof. The browser console reported no page errors in the exercised flow.

Still requiring deployment access: Pi/systemd and role filesystem permissions, trusted client HTTPS, real DNS/NTP and both ESP32 streams, Atlas role administration, real CF600/multi-tag operation and vendor integrity details, LED/buzzer/motor wiring plus independent cutoff, live Minew acquisition, actual external email/replies, and the group's real participant/task names. Local certificate trust was process-scoped for the synthetic SMTP test; no global trust bypass was installed.
