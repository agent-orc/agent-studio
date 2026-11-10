import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { parseArgs, usage } from './args.mjs';
import { COMMANDS } from './commands.mjs';
import { CredentialError, configDirectory, readCredential, refuseTokenCarriers } from './credential.mjs';
import { EXIT } from './exit-codes.mjs';
import { Output } from './output.mjs';
import { OperationError, StudioClient } from './studio.mjs';

export const VERSION = '1.0.0';
export const CONFIG_FILE = 'studio-op.json';
export const DEFAULT_URL = 'http://127.0.0.1:5031';
const CONFIG_KEYS = new Set(['url', 'credential', 'clientId']);
const GROUPS = { auth: ['status'], deploy: ['plan'], host: ['drain', 'status', 'resume'], log: ['append'], routing: ['set-step'] };
// These groups also run without a subcommand: `log` reads, `routing` shows.
const BARE_GROUPS = new Set(['log', 'routing']);

/** Splits argv into the command name and its arguments. */
function commandOf(argv) {
  const [first, second] = argv;
  if (!first || first.startsWith('--')) return { name: null, rest: argv };
  if (GROUPS[first]) {
    if (GROUPS[first].includes(second)) return { name: `${first} ${second}`, rest: argv.slice(2) };
    if (BARE_GROUPS.has(first)) return { name: first, rest: argv.slice(1) };
    return { name: `${first} ${second ?? ''}`.trim(), rest: argv.slice(2) };
  }
  return { name: first, rest: argv.slice(1) };
}

function readConfig(directory) {
  const file = path.join(directory, CONFIG_FILE);
  let text;
  try { text = fs.readFileSync(file, 'utf8'); }
  catch (error) {
    if (error.code === 'ENOENT') return {};
    throw usage('config-unreadable', `Cannot read ${file} (${error.code}).`);
  }
  let config;
  try { config = JSON.parse(text); } catch { throw usage('config-invalid', `${file} is not valid JSON.`); }
  if (!config || typeof config !== 'object' || Array.isArray(config)) throw usage('config-invalid', `${file} must hold a JSON object.`);
  const unknown = Object.keys(config).filter(key => !CONFIG_KEYS.has(key));
  if (unknown.length > 0)
    throw usage('config-invalid', `${file} has unknown keys: ${unknown.join(', ')}. Allowed: ${[...CONFIG_KEYS].join(', ')}. A token never goes into this file.`);
  return config;
}

export function helpText() {
  const width = Math.max(...Object.keys(COMMANDS).map(name => name.length));
  return [
    `studio-op ${VERSION}: operate Agent Studio from a coding-agent CLI or a terminal.`,
    '',
    'Usage: studio-op <command> [arguments] [--json] [--url <studio url>]',
    '',
    ...Object.entries(COMMANDS).map(([name, command]) => `  studio-op ${name.padEnd(width)}  ${command.summary}`),
    '',
    'The token is read only from ~/.config/agent-studio/operator-token (chmod 600),',
    'or operator-session when studio-op.json sets "credential": "operator-session".',
    'There is no token option and no token environment variable.',
    'Exit codes and setup: docs/operations/setup/studio-op.md',
  ].join('\n');
}

/**
 * Runs one studio-op invocation and returns its exit code. `env`, `home`
 * and the streams are injectable for tests; the binary passes the process.
 */
export async function main(argv, { env = process.env, home = os.homedir(), stdout = process.stdout,
  stderr = process.stderr } = {}) {
  const output = new Output({ stdout, stderr, json: argv.includes('--json') });
  const { name, rest } = commandOf(argv);
  try {
    // Before anything else: a token never arrives by environment or argument.
    refuseTokenCarriers({ env, argv });
    if (argv.includes('--version')) { stdout.write(`${VERSION}\n`); return EXIT.OK; }
    if (!name || name === 'help' || (argv.includes('--help') && !COMMANDS[name])) {
      stdout.write(`${helpText()}\n`);
      return name && name !== 'help' && !argv.includes('--help') ? EXIT.USAGE : EXIT.OK;
    }
    const command = COMMANDS[name];
    if (!command) throw usage('unknown-command', `Unknown command "${name}". Run studio-op --help.`);
    const { positionals, options } = parseArgs(rest, command.spec);
    if (options.help) { stdout.write(`studio-op ${name}: ${command.summary}\n`); return EXIT.OK; }

    const configDir = configDirectory(home);
    const config = readConfig(configDir);
    const url = options.url ?? env.STUDIO_OP_URL ?? config.url ?? DEFAULT_URL;
    const credential = readCredential({ home, config });
    if (credential) output.protect(credential.token);
    let client = null;
    let clientError = null;
    try { client = new StudioClient({ url, credential, clientId: config.clientId ?? 'local-default' }); }
    catch (error) {
      // auth status reports a missing or misplaced credential instead of failing on it.
      if (!command.local) throw error;
      clientError = error;
    }
    const ctx = { client, clientError, output, home, configDir, config, url, credential };
    const result = await command.run(ctx, positionals, options);
    output.result(name, result.data ?? {}, result.text, result.ok ?? true);
    return result.exitCode ?? EXIT.OK;
  } catch (error) {
    if (error instanceof CredentialError) {
      output.error(name, { code: error.code, message: error.message, exitCode: EXIT.CREDENTIAL });
      return EXIT.CREDENTIAL;
    }
    if (error instanceof OperationError) {
      const extra = error.status ? { status: error.status, answer: error.body } : (error.body ?? {});
      output.error(name, error, extra);
      return error.exitCode;
    }
    output.error(name, { code: 'failure', message: error?.message ?? String(error), exitCode: EXIT.FAILURE });
    return EXIT.FAILURE;
  }
}
