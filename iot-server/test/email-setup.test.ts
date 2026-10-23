import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, writeFile, stat, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import dotenv from 'dotenv';
import { loadConfiguration } from '../src/configuration.js';
import { gmailSettings, emailConfigFromValues, patchEnvFile, savePrivateEmailSettings,
  conflictingEmailOverrides } from '../src/email-setup.js';

const settings = () => gmailSettings('alerts@sender.test', 'abcd efgh ijkl mnop', 'owner@recipient.test');

test('Gmail setup derives both servers/ports and normalizes the app password', () => {
  const values = settings();
  assert.equal(values.SMTP_HOST, 'smtp.gmail.com');
  assert.equal(values.SMTP_PORT, '465');
  assert.equal(values.IMAP_HOST, 'imap.gmail.com');
  assert.equal(values.IMAP_PORT, '993');
  assert.equal(values.SMTP_PASSWORD, 'abcdefghijklmnop');
  assert.equal(values.SMTP_PASSWORD, values.IMAP_PASSWORD);
  assert.equal(values.SMTP_USER, values.IMAP_USER);
  const { config, recipient } = emailConfigFromValues(values);
  assert.equal(config.enabled, true);
  assert.equal(config.smtpPort, 465);
  assert.equal(config.imapPort, 993);
  assert.equal(recipient, 'owner@recipient.test');
});

test('invalid credentials and placeholders fail without echoing secrets', () => {
  assert.throws(() => gmailSettings('not-an-address', 'abcdefghijklmnop', 'owner@recipient.test'), /real mailbox/);
  assert.throws(() => gmailSettings('alerts@sender.test', 'a-normal-password', 'owner@recipient.test'), /16-character/);
  assert.throws(() => gmailSettings('owner@example.com', 'abcdefghijklmnop', 'owner@recipient.test'), /real mailbox/);
  assert.throws(() => emailConfigFromValues({ ...settings(), SMTP_PASSWORD: 'REPLACE_WITH_SECRET' }), error => {
    assert.ok(error instanceof Error);
    assert.match(error.message, /SMTP_PASSWORD/);
    assert.doesNotMatch(error.message, /REPLACE_WITH_SECRET/);
    return true;
  });
});

test('patching preserves MongoDB, MQTT, API settings and comments; updated duplicates are removed', () => {
  const original = '# keep this comment\nMONGODB_URI="mongodb://test-only"\nMQTT_PASSWORD="broker-test-only"\n' +
    'IOT_API_KEY="api-test-only"\nexport SMTP_USER="old"\nSMTP_USER="duplicate"\nEMAIL_ENABLED=false\n';
  const result = patchEnvFile(original, settings());
  const parsed = dotenv.parse(result);
  assert.equal(parsed.MONGODB_URI, 'mongodb://test-only');
  assert.equal(parsed.MQTT_PASSWORD, 'broker-test-only');
  assert.equal(parsed.IOT_API_KEY, 'api-test-only');
  assert.deepEqual(Object.fromEntries(Object.keys(settings()).map(key => [key, parsed[key]])), settings());
  assert.ok(result.includes('# keep this comment'));
  assert.equal(result.match(/^SMTP_USER=/gm)?.length, 1);
  assert.throws(() => patchEnvFile(original, { SMTP_USER: 'injected\nOTHER=value' }), /single-line/);
});

test('saving is private and preserves unrelated configuration', async t => {
  const directory = await mkdtemp(join(tmpdir(), 'northstar-email-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const file = join(directory, '.env');
  await writeFile(file, 'MONGODB_URI="mongodb://test-only"\nEMAIL_ENABLED=false\n', { mode: 0o644 });
  await savePrivateEmailSettings(file, settings());
  const parsed = dotenv.parse(await readFile(file));
  assert.equal(parsed.MONGODB_URI, 'mongodb://test-only');
  assert.equal(parsed.EMAIL_ENABLED, 'true');
  if (process.platform !== 'win32') assert.equal((await stat(file)).mode & 0o777, 0o600);
});

test('setup and startup share process > explicit file > project > user config precedence', async t => {
  const directory = await mkdtemp(join(tmpdir(), 'northstar-config-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const root = join(directory, 'checkout');
  const userConfig = join(directory, 'config');
  await mkdir(root);
  await mkdir(join(userConfig, 'northstar'), { recursive: true });
  const explicit = join(directory, 'private.env');
  await writeFile(explicit, 'SMTP_USER=explicit\nSMTP_HOST=explicit-host\n');
  await writeFile(join(root, '.env'), 'SMTP_USER=project\nSMTP_HOST=project-host\nIOT_API_KEY=project-key\n');
  await writeFile(join(userConfig, 'northstar/credentials.env'), 'SMTP_USER=fallback\nMONGODB_URI=mongodb://fallback\n');
  const values = loadConfiguration(root, { SMTP_USER: 'process', MONGODB_ENV_FILE: explicit, XDG_CONFIG_HOME: userConfig });
  assert.equal(values.SMTP_USER, 'process');
  assert.equal(values.SMTP_HOST, 'explicit-host');
  assert.equal(values.IOT_API_KEY, 'project-key');
  assert.equal(values.MONGODB_URI, 'mongodb://fallback');
});

test('conflicting environment overrides are reported by key only', () => {
  assert.deepEqual(conflictingEmailOverrides(settings(), { SMTP_PASSWORD: 'another-test-password', SMTP_HOST: 'smtp.gmail.com' }), ['SMTP_PASSWORD']);
});
