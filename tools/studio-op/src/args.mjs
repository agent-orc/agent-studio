import { EXIT } from './exit-codes.mjs';
import { OperationError } from './studio.mjs';

/** Options every command accepts. */
export const GLOBAL_OPTIONS = Object.freeze({ json: 'boolean', url: 'string', help: 'boolean' });

/**
 * Parses `argv` against an option spec ({ name: 'string' | 'boolean' |
 * 'list' }). Returns { positionals, options }. Unknown options are usage
 * errors, so a mistyped flag never silently changes what a command does.
 */
export function parseArgs(argv, spec) {
  const options = {};
  const positionals = [];
  const known = { ...GLOBAL_OPTIONS, ...spec };
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index];
    if (arg === '--') { positionals.push(...argv.slice(index + 1)); break; }
    if (!arg.startsWith('--')) { positionals.push(arg); continue; }
    const [rawName, inline] = arg.slice(2).split(/=(.*)/s, 2);
    const name = rawName.replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
    const kind = known[name];
    if (!kind) throw usage('unknown-option', `Unknown option --${rawName}.`);
    if (kind === 'boolean') {
      if (inline !== undefined) throw usage('invalid-option', `--${rawName} takes no value.`);
      options[name] = true;
      continue;
    }
    const value = inline ?? argv[++index];
    if (value === undefined || (inline === undefined && value.startsWith('--')))
      throw usage('missing-option-value', `--${rawName} needs a value.`);
    if (kind === 'list') (options[name] ??= []).push(value);
    else options[name] = value;
  }
  return { positionals, options };
}

export function usage(code, message) {
  return new OperationError(code, message, { exitCode: EXIT.USAGE });
}

export function required(options, name, flag = name) {
  const value = options[name];
  if (value === undefined || value === '') throw usage('missing-option', `--${flag} is required.`);
  return value;
}

export function positiveInteger(value, flag, { min = 1, max = Number.MAX_SAFE_INTEGER } = {}) {
  const number = Number(value);
  if (!Number.isInteger(number) || number < min || number > max)
    throw usage('invalid-option', `--${flag} must be a whole number from ${min} to ${max}.`);
  return number;
}
