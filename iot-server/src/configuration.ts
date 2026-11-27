import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { homedir } from 'node:os';
import { fileURLToPath } from 'node:url';
import dotenv from 'dotenv';

export const projectRoot = fileURLToPath(new URL('../../', import.meta.url));

// Keep startup, email setup and diagnostics on exactly the same configuration.
export function loadConfiguration(root = projectRoot, environment: NodeJS.ProcessEnv = process.env) {
  const values = { ...environment };
  if (environment.NORTHSTAR_CONFIG_FILE && !existsSync(environment.NORTHSTAR_CONFIG_FILE))
    throw new Error('NORTHSTAR_CONFIG_FILE must name a readable service configuration file.');
  const paths = environment.NORTHSTAR_CONFIG_FILE ? [environment.NORTHSTAR_CONFIG_FILE]
    : [environment.MONGODB_ENV_FILE, resolve(root, '.env'),
      resolve(environment.XDG_CONFIG_HOME || resolve(homedir(), '.config'), 'northstar/credentials.env')];
  for (const path of paths) {
    if (!path || !existsSync(path)) continue;
    for (const [key, value] of Object.entries(dotenv.parse(readFileSync(path)))) {
      if (values[key] === undefined) values[key] = value;
    }
  }
  return values;
}
