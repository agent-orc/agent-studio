import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

// Leave one byte of sockaddr_un.sun_path for the terminating NUL.
const limit = process.platform === 'linux' ? 107 : 103;
const socketName = 'sign.sock';

export function checkSocketPath(endpoint) {
  const size = Buffer.byteLength(endpoint);
  if (size > limit) throw new Error(`Signer socket path "${endpoint}" uses ${size} bytes (limit ${limit}).`);
}

/** Every resident owns a fresh directory, including on the shared /tmp fallback. */
export function createSocketPath(configDir, runtime = process.env.XDG_RUNTIME_DIR, temp = os.tmpdir()) {
  const uid = process.getuid();
  const installation = crypto.createHash('sha256').update(import.meta.url + '\0' + path.resolve(configDir))
    .digest('hex').slice(0, 12);
  for (const [base, name] of [[runtime, 'studio-op-agent-'], [temp, 'studio-op-agent-'],
    ['/tmp', `studio-op-${uid}-${installation}-`]]) {
    if (!base || !path.isAbsolute(base)) continue;
    const prefix = path.join(base, name);
    const candidate = path.join(prefix + 'XXXXXX', socketName);
    if (Buffer.byteLength(candidate) > limit) continue;
    let directory;
    try {
      const parent = fs.statSync(base);
      if (!parent.isDirectory() || (parent.uid !== uid && parent.uid !== 0)
        || ((parent.mode & 0o022) && !(parent.mode & 0o1000))) continue;
      directory = fs.mkdtempSync(prefix);
    } catch (error) {
      if (['ENOENT', 'ENOTDIR', 'EACCES', 'EROFS'].includes(error.code) && name === 'studio-op-agent-') continue;
      throw error;
    }
    try {
      const stat = fs.lstatSync(directory);
      if (!stat.isDirectory() || stat.uid !== uid || (stat.mode & 0o777) !== 0o700)
        throw new Error(`Signer needs a private, user-owned directory with mode 0700: ${directory}`);
      const endpoint = path.join(directory, socketName);
      checkSocketPath(endpoint);
      return endpoint;
    } catch (error) {
      fs.rmdirSync(directory);
      throw error;
    }
  }
  throw new Error(`Cannot place a private signer socket in ${runtime || '(no runtime directory)'}, ${temp} or /tmp (path limit ${limit} bytes).`);
}
