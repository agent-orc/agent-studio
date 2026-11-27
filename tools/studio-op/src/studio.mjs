import { signFromAgent, stopAgent } from './agent.mjs';
import { CredentialError } from './credential.mjs';
import { EXIT, httpExitCode } from './exit-codes.mjs';

const REQUEST_TIMEOUT_MS = 30_000;

/** A failed operation with a stable code, the exit code, and the Studio answer. */
export class OperationError extends Error {
  constructor(code, message, { exitCode = EXIT.FAILURE, status = null, body = null } = {}) {
    super(message);
    this.name = 'OperationError';
    this.code = code;
    this.exitCode = exitCode;
    this.status = status;
    this.body = body;
  }
}

function isLoopback(hostname) {
  return hostname === 'localhost' || hostname === '::1' || hostname === '[::1]' || /^127\./.test(hostname);
}

/**
 * The HTTP client of one invocation. Every request carries the bearer from
 * the token file, when there is one, and the client attribution header. A
 * token goes only over HTTPS or to a loopback address; a networked origin
 * without a token file is refused before the first request.
 */
export class StudioClient {
  constructor({ url, credential, clientId, anonymous = false, configDir }) {
    let base;
    try { base = new URL(url); }
    catch { throw new OperationError('invalid-url', `The Studio URL "${url}" is not a URL.`, { exitCode: EXIT.USAGE }); }
    if (base.protocol !== 'http:' && base.protocol !== 'https:')
      throw new OperationError('invalid-url', 'The Studio URL must use HTTP or HTTPS.', { exitCode: EXIT.USAGE });
    const loopback = isLoopback(base.hostname);
    if (credential && base.protocol === 'http:' && !loopback)
      throw new CredentialError('token-requires-https', 'A token is sent only over HTTPS or to a loopback address.');
    // A sign-in receives a session, so it needs the same transport a token needs.
    if (anonymous && base.protocol === 'http:' && !loopback)
      throw new CredentialError('token-requires-https', 'A sign-in receives a session only over HTTPS or from a loopback address.');
    if (!anonymous && !credential && !(base.protocol === 'http:' && loopback))
      throw new CredentialError('token-file-missing',
        'A networked Studio needs a token file. Run studio-op auth status for the path and setup.');
    this.base = base;
    this.credential = credential;
    this.clientId = clientId;
    this.configDir = configDir;
  }

  headers(extra = {}) {
    return {
      Accept: 'application/json',
      'X-Client-Id': this.clientId,
      ...(this.credential ? { Authorization: `Bearer ${this.credential.token}` } : {}),
      ...extra,
    };
  }

  async signedHeaders(method, url, extra = {}) {
    const proof = this.credential?.token.startsWith('oas.') && this.configDir
      ? await signFromAgent(this.configDir, this.credential.token, method, url) : null;
    return this.headers({ ...extra, ...(proof ? { DPoP: proof } : {}) });
  }

  url(path, query = {}) {
    // Keep a path prefix of the configured origin, such as https://host/studio/.
    const root = this.base.href.endsWith('/') ? this.base.href : `${this.base.href}/`;
    if (!path.startsWith('/') || path.startsWith('//') || /[\\#]/.test(path))
      throw new OperationError('invalid-path', 'Use an absolute API path on this Studio origin.', { exitCode: EXIT.USAGE });
    const target = new URL(path.replace(/^\/+/, ''), root);
    if (target.origin !== this.base.origin)
      throw new OperationError('invalid-path', 'The request must stay on the Studio origin.', { exitCode: EXIT.USAGE });
    for (const [key, value] of Object.entries(query))
      if (value !== undefined && value !== null && value !== '') target.searchParams.set(key, String(value));
    return target;
  }

  /** Sends one request and returns { status, body, headers } without judging the status. */
  async send(method, path, { query, body, headers, signal } = {}) {
    let response;
    const target = this.url(path, query);
    const requestHeaders = await this.signedHeaders(method, target, { ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}), ...headers });
    try {
      response = await fetch(target, {
        method, redirect: 'manual',
        headers: requestHeaders,
        body: body !== undefined ? JSON.stringify(body) : undefined,
        signal: signal ?? AbortSignal.timeout(REQUEST_TIMEOUT_MS),
      });
    } catch (error) {
      throw new OperationError('studio-unreachable',
        `Studio at ${this.base.origin} did not answer (${error.cause?.code ?? error.name}).`, { exitCode: EXIT.UNREACHABLE });
    }
    if (response.status === 401 && requestHeaders.DPoP && this.configDir) await stopAgent(this.configDir);
    const text = await response.text();
    let parsed = null;
    if (text) {
      try { parsed = JSON.parse(text); } catch { parsed = { message: text }; }
    }
    return { status: response.status, body: parsed, headers: response.headers };
  }

  /** Sends one request and throws an {@link OperationError} for any non-2xx answer. */
  async request(method, path, options = {}) {
    const answer = await this.send(method, path, options);
    if (answer.status >= 200 && answer.status < 300) return answer.body;
    throw failure(method, path, answer);
  }

  /** Opens the decision stream; returns the fetch response with a readable body. */
  async stream(path, { query, signal }) {
    let response;
    const target = this.url(path, query);
    const requestHeaders = await this.signedHeaders('GET', target, { Accept: 'text/event-stream' });
    try {
      response = await fetch(target, {
        redirect: 'manual', headers: requestHeaders,
        signal,
      });
    } catch (error) {
      if (signal?.aborted) throw error;
      throw new OperationError('studio-unreachable',
        `Studio at ${this.base.origin} did not answer (${error.cause?.code ?? error.name}).`, { exitCode: EXIT.UNREACHABLE });
    }
    if (response.status === 401 && requestHeaders.DPoP && this.configDir) await stopAgent(this.configDir);
    if (!response.ok) {
      const text = await response.text();
      let body = null;
      try { body = JSON.parse(text); } catch { body = { message: text }; }
      throw failure('GET', path, { status: response.status, body });
    }
    return response;
  }
}

export function failure(method, path, { status, body }) {
  const slug = value => typeof value === 'string' && /^[a-z0-9-]+$/i.test(value) ? value : null;
  // Some routes answer `error: "<code>: <sentence>"`.
  const prefixed = typeof body?.error === 'string' ? body.error.match(/^([a-z0-9]+(?:-[a-z0-9]+)+): /)?.[1] ?? null : null;
  const code = slug(body?.error) ?? slug(body?.code) ?? prefixed ?? (status === 401 ? 'unauthenticated' : status === 403 ? 'forbidden'
      : status === 404 ? 'not-found' : `http-${status}`);
  const detail = body?.message ?? (typeof body?.error === 'string' && body.error !== code ? body.error : null);
  const login = ['session-ended', 'renewal-device-key-missing', 'limit-lowered', 'dpop-required', 'dpop-key-mismatch'].includes(code)
    ? ' Run studio-op login.' : '';
  return new OperationError(code, `${method} ${path} answered ${status} ${code}${detail ? `: ${detail}` : ''}${login}`,
    { exitCode: httpExitCode(status, code), status, body });
}
