import { redact } from './credential.mjs';

/**
 * The only writer of studio-op. Every line passes through {@link redact}
 * with the loaded token, so no output carries it, even when a Studio answer
 * echoes it. A process-level guard covers anything else that writes to the
 * streams (a runtime warning, for example).
 */
export class Output {
  constructor({ stdout = process.stdout, stderr = process.stderr, json = false } = {}) {
    this.stdout = stdout;
    this.stderr = stderr;
    this.json = json;
    this.token = null;
  }

  protect(token) {
    this.token = token;
    guardStreams(token);
  }

  clean(text) {
    return redact(text, this.token);
  }

  /** Progress and notes: always stderr, so --json keeps stdout parseable. */
  note(text) {
    this.stderr.write(this.clean(`${text}\n`));
  }

  /** The command result: one JSON document with --json, text otherwise. */
  result(command, data, text, ok = true) {
    if (this.json) this.stdout.write(this.clean(`${JSON.stringify({ ok, command, ...data }, null, 2)}\n`));
    else if (text) this.stdout.write(this.clean(`${text}\n`));
  }

  /** A refusal or failure: JSON on stdout with --json, a line on stderr otherwise. */
  error(command, error, { detailText, ...extra } = {}) {
    if (this.json) {
      this.stdout.write(this.clean(`${JSON.stringify({
        ok: false, command, error: error.code ?? 'failure', message: error.message, exitCode: error.exitCode, ...extra,
      }, null, 2)}\n`));
    } else {
      this.stderr.write(this.clean(`studio-op ${command ?? ''}: ${error.code ?? 'failure'}: ${error.message}\n`));
      if (detailText) this.stderr.write(this.clean(`${detailText}\n`));
    }
  }
}

let guarded = null;

function guardStreams(token) {
  if (!token || guarded === token) return;
  const first = guarded === null;
  guarded = token;
  if (!first) return;
  for (const stream of [process.stdout, process.stderr]) {
    const write = stream.write.bind(stream);
    stream.write = (chunk, ...rest) => write(
      typeof chunk === 'string' ? redact(chunk, guarded)
        : Buffer.isBuffer(chunk) && chunk.includes(guarded) ? redact(chunk.toString(), guarded) : chunk,
      ...rest);
  }
}
