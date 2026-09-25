/** Keeps a card key and the tail visible when a long generated ref is narrow. */
export function compactGitRef(value: string, limit = 44): string {
  if (value.length <= limit) return value;
  const key = value.match(/[A-Z][A-Z0-9]+-\d+/)?.[0];
  const tail = value.split('/').slice(-2).join('/');
  if (key && tail && !tail.includes(key)) return `…/${key}/…/${tail}`;
  const tailLength = Math.max(18, limit - 15);
  return `${value.slice(0, 12)}…${value.slice(-tailLength)}`;
}
