import { mkdir, writeFile, chmod } from 'node:fs/promises';
import { resolve } from 'node:path';
import { homedir } from 'node:os';

export type EmailStage = 'smtp-sign-in' | 'imap-inbox' | 'smtp-submit';
export interface EmailDiagnostic {
  stage: EmailStage; code: string; responseCode?: number; enhancedCode?: string;
  command?: string; retryable: boolean; guidance: string;
}

// Allowlisted codes only. Never retain raw errors, responses, messages, stacks,
// mailbox addresses, AUTH payloads, or passwords in diagnostics.
export function diagnoseEmailError(stage: EmailStage, error: unknown): EmailDiagnostic {
  const value = (error && typeof error === 'object' ? error : {}) as Record<string, unknown>;
  const knownCodes = new Set(['EAUTH', 'ECONNECTION', 'ESOCKET', 'ETIMEDOUT', 'EENVELOPE', 'EMESSAGE', 'ESTREAM',
    'ENOTFOUND', 'EAI_AGAIN', 'ECONNRESET', 'ECONNREFUSED', 'ECONNABORTED', 'EPIPE',
    'AUTHENTICATIONFAILED', 'AUTHORIZATIONFAILED', 'LOGINFAILED', 'NO', 'BAD', 'UNAVAILABLE', 'NOPERM', 'OVERQUOTA',
    'CERT_HAS_EXPIRED', 'CERT_NOT_YET_VALID', 'DEPTH_ZERO_SELF_SIGNED_CERT', 'SELF_SIGNED_CERT_IN_CHAIN',
    'UNABLE_TO_VERIFY_LEAF_SIGNATURE', 'ERR_TLS_CERT_ALTNAME_INVALID', 'ERR_SSL_WRONG_VERSION_NUMBER']);
  const safeCode = (code: unknown) => typeof code === 'string' && knownCodes.has(code) ? code : undefined;
  const code = safeCode(value.code) || safeCode(value.serverResponseCode) || 'UNKNOWN';
  const responseCode = typeof value.responseCode === 'number' && Number.isInteger(value.responseCode)
    && value.responseCode >= 100 && value.responseCode <= 599 ? value.responseCode : undefined;
  const enhancedCode = typeof value.response === 'string' ? value.response.match(/\b([245]\.\d{1,3}\.\d{1,3})\b/)?.[1] : undefined;
  const commands = ['CONN', 'CONNECT', 'EHLO', 'HELO', 'STARTTLS', 'AUTH PLAIN', 'AUTH LOGIN', 'AUTH XOAUTH2',
    'MAIL FROM', 'RCPT TO', 'DATA', 'QUIT', 'RSET'];
  const command = typeof value.command === 'string' && commands.includes(value.command) ? value.command : undefined;
  let retryable = false;
  let guidance = 'The request failed. Use these safe stage/code details to diagnose it; no password was logged.';
  if (code === 'EAUTH' || code.includes('AUTHENTICATION') || value.authenticationFailed === true
    || responseCode === 534 || responseCode === 535 || enhancedCode === '5.7.9') {
    guidance = 'Google rejected sign-in. Generate a fresh app password for the exact Gmail address entered, with 2-Step Verification enabled. A normal password will not work; school/work policies may prohibit app passwords.';
  } else if (['ENOTFOUND', 'EAI_AGAIN'].includes(code)) {
    retryable = true;
    guidance = 'Mail-server DNS lookup failed. Check the hotspot/internet connection and retry.';
  } else if (['ETIMEDOUT', 'ECONNECTION', 'ECONNRESET', 'ECONNREFUSED', 'ECONNABORTED', 'ESOCKET', 'EPIPE'].includes(code)) {
    retryable = true;
    guidance = 'The mail connection timed out or was interrupted. Retry on the hotspot; SMTP can also use port 587 with required STARTTLS.';
  } else if (/CERT|TLS|SSL|SIGNATURE/.test(code)) {
    guidance = 'TLS certificate validation failed. Check the system clock and network; do not disable certificate verification.';
  } else if (responseCode && responseCode >= 400 && responseCode < 500) {
    retryable = true;
    guidance = 'The mail server temporarily refused the request. Wait a little and retry; sending limits or provider throttling may apply.';
  } else if (code === 'EENVELOPE' || [550, 551, 553].includes(responseCode || 0)) {
    guidance = 'The server rejected the sender or recipient. Check both addresses and recipient mail restrictions; a valid login alone does not guarantee sending permission.';
  } else if (responseCode === 552) {
    guidance = 'The server reported a quota/size limit. Check the sending account quota and Google sending limits before retrying.';
  } else if (responseCode && responseCode >= 500) {
    guidance = 'The mail provider rejected the message/request. Check the account sending policy, recipient restrictions and safe response codes shown here.';
  }
  return { stage, code, responseCode, enhancedCode, command, retryable, guidance };
}

export function formatEmailDiagnostic(diagnostic: EmailDiagnostic) {
  const codes = [diagnostic.code, diagnostic.responseCode, diagnostic.enhancedCode, diagnostic.command].filter(Boolean).join(' / ');
  return `${diagnostic.stage}: ${codes}. ${diagnostic.guidance}`;
}

export async function saveEmailDiagnostics(errors: EmailDiagnostic[], stateHome = process.env.XDG_STATE_HOME || resolve(homedir(), '.local/state')) {
  const directory = resolve(stateHome, 'northstar');
  await mkdir(directory, { recursive: true, mode: 0o700 });
  const path = resolve(directory, 'email-setup-diagnostic.json');
  await writeFile(path, JSON.stringify({ timestamp: new Date().toISOString(), errors }, null, 2) + '\n', { mode: 0o600 });
  await chmod(path, 0o600);
  return path;
}
