import { input, password, confirm, select } from '@inquirer/prompts';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { resolve } from 'node:path';
import { EmailService } from './email.js';
import { loadConfiguration, projectRoot } from './configuration.js';
import { emailAddress, isPlaceholder, gmailSettings, emailConfigFromValues,
  conflictingEmailOverrides, savePrivateEmailSettings } from './email-setup.js';
import { diagnoseEmailError, formatEmailDiagnostic, saveEmailDiagnostics,
  type EmailDiagnostic, type EmailStage } from './email-diagnostics.js';

const exec = promisify(execFile);
class SetupFailure extends Error {}
const validAddress = (value: string) => emailAddress.safeParse(value).success && !isPlaceholder(value);
const askAppPassword = () => password({ message: 'Google app password (16 characters; spaces are OK):', mask: true,
  validate: value => /^[a-zA-Z0-9]{16}$/.test(value.replace(/\s/g, ''))
    || 'Enter the 16-character app password, not your normal Gmail password.' });

async function recordDiagnostics(errors: EmailDiagnostic[]) {
  if (!errors.length) return;
  try { console.error('Safe diagnostic (no addresses/passwords): ' + await saveEmailDiagnostics(errors)); }
  catch { console.error('Could not save diagnostic; the safe error codes are shown above.'); }
}

async function restartOwnService(): Promise<boolean> {
  try {
    const { stdout } = await exec('systemctl', ['show', 'northstar-iot', '--property=WorkingDirectory', '--value'], { timeout: 5000 });
    // Never restart an unrelated installation with the same service name.
    if (!stdout.trim() || resolve(stdout.trim()) !== resolve(projectRoot)) return false;
    await exec('systemctl', ['is-active', '--quiet', 'northstar-iot'], { timeout: 5000 });
    if (process.getuid?.() === 0) {
      await exec('systemctl', ['restart', 'northstar-iot'], { timeout: 20000 });
    } else {
      await exec('sudo', ['-n', 'systemctl', 'restart', 'northstar-iot'], { timeout: 20000 });
    }
    return true;
  } catch { return false; }
}

async function waitForReplyPolling(values: NodeJS.ProcessEnv) {
  for (let attempt = 0; attempt < 10; attempt++) {
    try {
      const response = await fetch(`http://127.0.0.1:${values.IOT_PORT || 3100}/api/status`, {
        headers: { 'X-Iot-Key': values.IOT_API_KEY || '' }, signal: AbortSignal.timeout(2000)
      });
      if (response.ok && (await response.json()).emailConnected === true) return true;
    } catch { /* service may still be starting */ }
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  return false;
}

async function main() {
  const args = process.argv.slice(2);
  if (args.includes('--help')) {
    console.log('npm run setup:email  — guided Gmail setup (private, masked password)');
    console.log('npm run check:email  — verify existing SMTP/IMAP settings without sending or changing anything');
    return;
  }
  if (args.some(arg => arg !== '--check')) throw new SetupFailure('Unknown option. Use --help.');
  const values = loadConfiguration();
  if (args.includes('--check')) {
    let parsed;
    try { parsed = emailConfigFromValues(values); }
    catch { throw new SetupFailure('Email is not configured yet. Run npm --prefix iot-server run setup:email.'); }
    const service = new EmailService(parsed.config);
    try {
      const errors: EmailDiagnostic[] = [];
      const result = await service.verifyConnections(error => { errors.push(error); console.error(formatEmailDiagnostic(error)); });
      await recordDiagnostics(errors);
      console.log(`SMTP sign-in: ${result.smtp ? 'OK' : 'FAILED'}`);
      console.log(`IMAP inbox: ${result.imap ? 'OK' : 'FAILED'}`);
      console.log(`Automatic alerts: ${parsed.config.enabled ? 'enabled' : 'disabled'}`);
      if (!result.smtp || !result.imap) process.exitCode = 1;
    } finally { service.close(); }
    return;
  }
  if (!process.stdin.isTTY || !process.stdout.isTTY) {
    throw new SetupFailure('Run setup:email in an interactive terminal. Never pass a password as a command-line argument.');
  }
  console.log('\nNorthstar Gmail setup\n');
  console.log('Manual Google step: enable 2-Step Verification, then create an app password:');
  console.log('  https://myaccount.google.com/apppasswords');
  console.log('A dedicated mailbox is recommended. A personal Gmail account also works.');
  console.log('If Google hides App passwords, check 2-Step Verification or account/admin restrictions.');
  console.log('Use another mailbox (e.g. your school address) to receive alerts and reply.');
  console.log('The password is masked and saved only in a private configuration file, never in command history.');
  console.log('Setup never starts the physical fan.\n');

  const sender = (await input({ message: 'Gmail/application mailbox address:',
    default: validAddress(values.SMTP_USER || '') ? values.SMTP_USER : undefined,
    validate: value => validAddress(value) || 'Enter a real email address.' })).trim();
  const recipient = (await input({ message: 'Alert recipient / approval address (different mailbox):',
    default: validAddress(values.ALERT_TO || '') && values.ALERT_TO?.toLowerCase() !== sender.toLowerCase() ? values.ALERT_TO : undefined,
    validate: value => validAddress(value) && value.trim().toLowerCase() !== sender.toLowerCase()
      || 'Use a real, different mailbox so replies arrive as new incoming mail.' })).trim();
  const appPassword = await askAppPassword();
  let settings = gmailSettings(sender, appPassword, recipient);
  const overrides = conflictingEmailOverrides(settings, process.env);
  if (overrides.length) {
    throw new SetupFailure('These process environment keys override the setup file: ' + overrides.join(', ') + '. Unset them and rerun setup.');
  }
  const approved = await confirm({ default: true,
    message: `Verify both connections, send one test to ${recipient}, and enable live temperature emails?` });
  if (!approved) { console.log('Cancelled. No settings changed or email sent.'); return; }
  while (true) {
    const conflicts = conflictingEmailOverrides(settings, process.env);
    if (conflicts.length) throw new SetupFailure('Unset overriding process environment keys: ' + conflicts.join(', '));
    const { config } = emailConfigFromValues(settings);
    const service = new EmailService(config);
    const errors: EmailDiagnostic[] = [];
    let submitted = false;
    let stage: EmailStage = 'smtp-sign-in';
    try {
      console.log(`Checking SMTP (port ${settings.SMTP_PORT}) and IMAP inbox access...`);
      const result = await service.verifyConnections(error => { errors.push(error); console.error(formatEmailDiagnostic(error)); });
      console.log(`SMTP sign-in: ${result.smtp ? 'OK' : 'FAILED'}`);
      console.log(`IMAP inbox: ${result.imap ? 'OK' : 'FAILED'}`);
      if (result.smtp && result.imap) {
        stage = 'smtp-submit';
        await service.sendSetupTest(settings.ALERT_TO);
        submitted = true;
      }
    } catch (error) {
      const diagnostic = diagnoseEmailError(stage, error);
      errors.push(diagnostic);
      console.error(formatEmailDiagnostic(diagnostic));
    } finally { service.close(); }
    if (submitted) break;
    await recordDiagnostics(errors);
    console.log('Settings were NOT changed. You can retry without re-entering everything.');
    const action = await select({ message: 'Next step (a retry can submit another setup test):', choices: [
      { name: 'Retry with the same details', value: 'retry' },
      { name: 'Enter a fresh Google app password', value: 'password' },
      { name: 'Correct the Gmail mailbox address', value: 'sender' },
      { name: 'Correct the alert recipient', value: 'recipient' },
      { name: `Switch SMTP to port ${settings.SMTP_PORT === '465' ? '587 (STARTTLS)' : '465 (TLS)'}`, value: 'port' },
      { name: 'Cancel without changing configuration', value: 'cancel' }
    ] });
    if (action === 'cancel') { console.log('Cancelled. No configuration changed.'); return; }
    if (action === 'password') {
      const updated = gmailSettings(settings.SMTP_USER, await askAppPassword(), settings.ALERT_TO);
      settings = { ...updated, SMTP_PORT: settings.SMTP_PORT };
    }
    if (action === 'sender') {
      const updated = await input({ message: 'Gmail address that generated the app password:', default: settings.SMTP_USER,
        validate: value => validAddress(value) && value.trim().toLowerCase() !== settings.ALERT_TO.toLowerCase()
          || 'Enter a real mailbox different from the alert recipient.' });
      settings = { ...settings, SMTP_USER: updated.trim(), IMAP_USER: updated.trim(), SMTP_FROM: updated.trim() };
    }
    if (action === 'recipient') {
      const updated = await input({ message: 'Alert recipient:', default: settings.ALERT_TO,
        validate: value => validAddress(value) && value.trim().toLowerCase() !== settings.SMTP_USER.toLowerCase()
          || 'Enter a real, different recipient mailbox.' });
      if (!await confirm({ message: `Send the test and future temperature alerts to ${updated.trim()}?`, default: true })) {
        console.log('Cancelled. No configuration changed.');
        return;
      }
      settings = { ...settings, ALERT_TO: updated.trim() };
    }
    if (action === 'port') settings = { ...settings, SMTP_PORT: settings.SMTP_PORT === '465' ? '587' : '465' };
  }
  console.log('Test submitted to SMTP. Check the recipient inbox/spam folder to confirm delivery.');
  const target = resolve(process.env.MONGODB_ENV_FILE || resolve(projectRoot, '.env'));
  try { await savePrivateEmailSettings(target, settings); }
  catch { throw new SetupFailure('Could not save the private settings file. Check its permissions; the test may have been sent, but setup was not applied.'); }
  console.log(`Saved private settings (mode 600): ${target}`);
  if (await restartOwnService()) {
    console.log('IoT service restarted automatically.');
    console.log(await waitForReplyPolling({ ...values, ...settings })
      ? 'Live reply polling connected. Temperature alerts are enabled.'
      : 'Service restarted; reply polling is not confirmed yet. Run npm --prefix iot-server run check:email.');
  } else {
    console.log('Settings saved. Restart the running IoT service to apply them.');
    console.log('For the supplied systemd setup: sudo systemctl restart northstar-iot');
  }
  console.log('For the end-to-end test, reply NO to a real temperature alert, keeping its subject.');
  console.log('YES requires a wired/running fan controller; a reply to the setup test cannot activate anything.');
}

main().catch(error => {
  if (error?.name === 'ExitPromptError') console.error('\nSetup cancelled.');
  else console.error(error instanceof SetupFailure ? error.message : 'Email setup failed safely. No password is displayed; retry from an interactive terminal.');
  process.exitCode = 1;
});
