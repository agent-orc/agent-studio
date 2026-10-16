import http from 'node:http';
import https from 'node:https';

/** Local requests keep their client attribution. Networked requests add a bearer. */
export function taskApiRequest(options, callback) {
  const base = process.env.TASKBOARD_URL
    ?? `http://${options.hostname}:${options.port}`;
  const target = new URL(options.path, base);
  if (target.protocol !== 'http:' && target.protocol !== 'https:')
    throw new Error('TASKBOARD_URL must use HTTP or HTTPS.');
  const token = process.env.AGENT_STUDIO_API_TOKEN;
  const headers = { ...options.headers, ...(token ? { Authorization: `Bearer ${token}` } : {}) };
  return (target.protocol === 'https:' ? https : http).request(target,
    { method: options.method, headers }, callback);
}
