import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, stat, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { diagnoseEmailError, formatEmailDiagnostic, saveEmailDiagnostics } from '../src/email-diagnostics.js';

test('authentication failures identify the stage and safe Google response codes', () => {
  const diagnostic = diagnoseEmailError('smtp-sign-in', { code: 'EAUTH', responseCode: 535,
    response: '535 5.7.8 authentication failed', command: 'AUTH PLAIN' });
  assert.equal(diagnostic.stage, 'smtp-sign-in');
  assert.equal(diagnostic.code, 'EAUTH');
  assert.equal(diagnostic.responseCode, 535);
  assert.equal(diagnostic.enhancedCode, '5.7.8');
  assert.equal(diagnostic.retryable, false);
  assert.match(formatEmailDiagnostic(diagnostic), /fresh app password/);
  assert.match(formatEmailDiagnostic(diagnostic), /exact Gmail address/);
  const imap = diagnoseEmailError('imap-inbox', { serverResponseCode: 'AUTHENTICATIONFAILED', authenticationFailed: true });
  assert.match(imap.guidance, /sign-in/);
});

test('message rejection and network errors have distinct actionable guidance', () => {
  const rejection = diagnoseEmailError('smtp-submit', { code: 'EENVELOPE', responseCode: 550, command: 'RCPT TO' });
  assert.equal(rejection.stage, 'smtp-submit');
  assert.match(rejection.guidance, /sender or recipient/);
  const network = diagnoseEmailError('smtp-sign-in', { code: 'ETIMEDOUT' });
  assert.equal(network.retryable, true);
  assert.match(network.guidance, /587/);
  assert.equal(diagnoseEmailError('smtp-submit', { responseCode: 421 }).retryable, true);
  assert.match(diagnoseEmailError('smtp-submit', { responseCode: 552 }).guidance, /quota/);
  assert.match(diagnoseEmailError('smtp-sign-in', { code: 'CERT_HAS_EXPIRED' }).guidance, /do not disable/);
});

test('raw credentials, email addresses, AUTH payloads and unknown codes are never retained', () => {
  const secret = 'ABCDEFGHIJKLMNOP';
  const address = 'private@recipient.test';
  const diagnostic = diagnoseEmailError('smtp-submit', {
    code: secret, message: `${address} ${secret}`, stack: secret,
    response: `535 5.7.8 ${address} ${secret}`, command: `AUTH PLAIN ${secret}`,
    user: address, password: secret
  });
  assert.equal(diagnostic.code, 'UNKNOWN');
  assert.equal(diagnostic.command, undefined);
  const output = JSON.stringify(diagnostic) + formatEmailDiagnostic(diagnostic);
  assert.ok(!output.includes(secret));
  assert.ok(!output.includes(address));
  assert.doesNotThrow(() => diagnoseEmailError('smtp-submit', null));
});

test('saved diagnostics are private and contain only safe failure details', async t => {
  const directory = await mkdtemp(join(tmpdir(), 'northstar-diagnostic-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const diagnostic = diagnoseEmailError('smtp-submit', { code: 'EAUTH', responseCode: 535,
    message: 'test-secret-never-write', response: '535 5.7.8 test-secret-never-write' });
  const file = await saveEmailDiagnostics([diagnostic], directory);
  const content = await readFile(file, 'utf8');
  assert.doesNotMatch(content, /test-secret-never-write/);
  assert.equal(JSON.parse(content).errors[0].stage, 'smtp-submit');
  if (process.platform !== 'win32') assert.equal((await stat(file)).mode & 0o777, 0o600);
});
