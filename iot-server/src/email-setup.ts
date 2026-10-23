import { readFile, mkdir, open, rename, unlink } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { randomUUID } from 'node:crypto';
import { z } from 'zod';
import type { EmailConfig } from './email.js';

export const gmailEndpoints = {
  SMTP_HOST: 'smtp.gmail.com', SMTP_PORT: '465',
  IMAP_HOST: 'imap.gmail.com', IMAP_PORT: '993'
};
export const emailAddress = z.string().trim().email();
export const isPlaceholder = (value: string) => /REPLACE_WITH|YOUR_|(?:^|@|\.)example\.(?:com|net|org)$/i.test(value);

export function gmailSettings(sender: string, password: string, recipient: string): Record<string, string> {
  const from = emailAddress.safeParse(sender);
  const to = emailAddress.safeParse(recipient);
  const appPassword = password.replace(/\s/g, '');
  if (!from.success || !to.success || isPlaceholder(sender) || isPlaceholder(recipient)) {
    throw new Error('Enter real mailbox and recipient email addresses.');
  }
  if (!/^[a-zA-Z0-9]{16}$/.test(appPassword)) {
    throw new Error('Use the 16-character Google app password, not your normal sign-in password.');
  }
  return { ...gmailEndpoints, EMAIL_ENABLED: 'true', SMTP_USER: from.data,
    SMTP_PASSWORD: appPassword, SMTP_FROM: from.data,
    IMAP_USER: from.data, IMAP_PASSWORD: appPassword, ALERT_TO: to.data };
}

export function emailConfigFromValues(values: NodeJS.ProcessEnv): { config: EmailConfig; recipient: string } {
  const configured = z.string().min(1).refine(value => !isPlaceholder(value));
  const port = z.coerce.number().int().min(1).max(65535);
  const parsed = z.object({
    EMAIL_ENABLED: z.enum(['true', 'false']).default('false'),
    SMTP_HOST: configured, SMTP_PORT: port.default(587), SMTP_USER: configured, SMTP_PASSWORD: configured,
    SMTP_FROM: emailAddress.refine(value => !isPlaceholder(value)),
    IMAP_HOST: configured, IMAP_PORT: port.default(993), IMAP_USER: configured, IMAP_PASSWORD: configured,
    ALERT_TO: emailAddress.refine(value => !isPlaceholder(value))
  }).safeParse(values);
  if (!parsed.success) {
    throw new Error('Missing/placeholder/invalid email settings: ' +
      [...new Set(parsed.error.issues.map(issue => issue.path.join('.')))].join(', '));
  }
  const env = parsed.data;
  return { recipient: env.ALERT_TO, config: { enabled: env.EMAIL_ENABLED === 'true',
    smtpHost: env.SMTP_HOST, smtpPort: env.SMTP_PORT, smtpUser: env.SMTP_USER,
    smtpPassword: env.SMTP_PASSWORD, from: env.SMTP_FROM,
    imapHost: env.IMAP_HOST, imapPort: env.IMAP_PORT, imapUser: env.IMAP_USER, imapPassword: env.IMAP_PASSWORD } };
}

export function conflictingEmailOverrides(settings: Record<string, string>, environment: NodeJS.ProcessEnv) {
  return Object.keys(settings).filter(key => environment[key] !== undefined && environment[key] !== settings[key]);
}

// Preserve database/broker/API settings and comments; remove duplicate updated keys.
export function patchEnvFile(text: string, settings: Record<string, string>) {
  const encode = (key: string) => {
    if (!/^[A-Z][A-Z0-9_]*$/.test(key)) throw new Error('Invalid environment key.');
    const value = settings[key];
    if (/[\r\n]/.test(value)) throw new Error('Email settings must be single-line values.');
    const quote = !value.includes("'") ? "'" : !value.includes('"') ? '"' : null;
    if (!quote) throw new Error('Email setting contains unsupported quoting.');
    return `${key}=${quote}${value}${quote}`;
  };
  const written = new Set<string>();
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  if (lines.at(-1) === '') lines.pop();
  const output = lines.flatMap(line => {
    const key = line.match(/^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=/)?.[1];
    if (!key || !Object.hasOwn(settings, key)) return [line];
    if (written.has(key)) return [];
    written.add(key);
    return [encode(key)];
  });
  for (const key of Object.keys(settings)) {
    if (!written.has(key)) output.push(encode(key));
  }
  return output.join('\n') + '\n';
}

export async function savePrivateEmailSettings(path: string, settings: Record<string, string>) {
  const target = resolve(path);
  let original = '';
  try { original = await readFile(target, 'utf8'); }
  catch (error) { if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error; }
  const content = patchEnvFile(original, settings);
  await mkdir(dirname(target), { recursive: true, mode: 0o700 });
  const temporary = `${target}.${randomUUID()}.tmp`;
  try {
    const handle = await open(temporary, 'wx', 0o600);
    try { await handle.writeFile(content); await handle.sync(); }
    finally { await handle.close(); }
    await rename(temporary, target);
  } finally { await unlink(temporary).catch(() => {}); }
}
