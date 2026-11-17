/** Words only, lower case: the comparison label-in-name checks use. */
function spokenWords(text: string): string {
  return text.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, ' ').trim();
}

/**
 * Accessible name for a control whose `aria-label` adds context to its
 * visible text. WCAG 2.5.3 (label in name) requires the name to contain the
 * visible text; starting with it lets speech-input users say what they see
 * while screen readers still hear the detail.
 */
export function nameWithVisibleText(visible: string, detail?: string | null): string {
  const shown = visible.replace(/\s+/g, ' ').trim();
  const extra = (detail ?? '').trim();
  if (!shown) return extra;
  if (!extra) return shown;
  const shownWords = spokenWords(shown);
  const extraWords = spokenWords(extra);
  if (extraWords === shownWords || extraWords.startsWith(`${shownWords} `)) return extra;
  if (shownWords.includes(extraWords)) return shown;
  return `${shown}, ${extra}`;
}
