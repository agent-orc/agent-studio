import { readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';

/**
 * Compatibility bridge for coding-agent-chat 0.4.1.
 *
 * The published markdown renderer is the canonical chat and activity surface,
 * but 0.4.1 sends unfenced CLI diffs and raw markup through the complete GFM
 * pipeline. Protect those conservative technical shapes before Marked sees
 * them. Keep the exact-version and occurrence guards so a package upgrade must
 * deliberately retire or update this bridge.
 */
const require = createRequire(import.meta.url);
const packageJsonPath = require.resolve('coding-agent-chat/package.json');
const packageRoot = dirname(packageJsonPath);
const packageJson = JSON.parse(readFileSync(packageJsonPath, 'utf8'));

if (packageJson.version !== '0.4.1') {
  throw new Error(
    `The coding-agent-chat technical-block compatibility patch expects 0.4.1, found ${packageJson.version}.`,
  );
}

function replaceExact(source, before, after, expectedCount, label) {
  const actualCount = source.split(before).length - 1;
  if (actualCount === 0 && source.includes(after)) return source;
  if (actualCount !== expectedCount) {
    throw new Error(
      `Could not apply ${label}: expected ${expectedCount} source occurrence(s), found ${actualCount}.`,
    );
  }
  return source.split(before).join(after);
}

const technicalProtectionSource = String.raw`/**
 * Fence recognizable CLI diffs and raw HTML/SVG before GFM parsing.
 * Existing fenced blocks pass through byte-for-byte. Starts deliberately
 * require a git header, a complete unified-diff header, a hunk header with
 * change evidence, or a block-level markup tag at the start of a line.
 */
function protectTechnicalMarkdown(markdown) {
    if (!markdown)
        return markdown;
    const newline = markdown.includes('\r\n') ? '\r\n' : '\n';
    const lines = markdown.replace(/\r\n?/g, '\n').split('\n');
    const out = [];
    let activeFence = null;
    for (let index = 0; index < lines.length;) {
        const line = lines[index];
        if (activeFence) {
            out.push(line);
            if (isTechnicalFenceClose(line, activeFence))
                activeFence = null;
            index++;
            continue;
        }
        const openingFence = technicalFenceOpening(line);
        if (openingFence) {
            activeFence = openingFence;
            out.push(line);
            index++;
            continue;
        }
        if (isUnfencedDiffStart(lines, index)) {
            const end = findUnfencedDiffEnd(lines, index);
            appendTechnicalFence(out, lines.slice(index, end), 'diff-autodetected');
            index = end;
            continue;
        }
        const markupRoot = technicalMarkupRoot(lines, index);
        if (markupRoot) {
            const end = findTechnicalMarkupEnd(lines, index, markupRoot);
            appendTechnicalFence(out, lines.slice(index, end), 'html-autodetected');
            index = end;
            continue;
        }
        out.push(line);
        index++;
    }
    return out.join(newline);
}
function technicalFenceOpening(line) {
    const match = /^ {0,3}((?:\x60){3,}|~{3,})(?:[^\n]*)$/.exec(line);
    if (!match)
        return null;
    return { marker: match[1][0], length: match[1].length };
}
function isTechnicalFenceClose(line, fence) {
    const marker = fence.marker === '~' ? '~' : '\\x60';
    return new RegExp('^ {0,3}(?:' + marker + '){' + fence.length + ',}[ \\t]*$').test(line);
}
function isUnfencedDiffStart(lines, index) {
    const line = lines[index] ?? '';
    if (isGitDiffHeader(line))
        return true;
    if (/^--- (?:\S|$)/.test(line)
        && /^\+\+\+ (?:\S|$)/.test(lines[index + 1] ?? '')
        && lines.slice(index + 2, index + 7).some(isUnifiedDiffHunk)) {
        return true;
    }
    if (!isUnifiedDiffHunk(line))
        return false;
    return lines.slice(index + 1, index + 7).some(candidate => /^[ +\\-]/.test(candidate));
}
function isGitDiffHeader(line) {
    return /^diff --git (?:"[^"]+"|\S+) (?:"[^"]+"|\S+)(?:\s.*)?$/.test(line);
}
function isUnifiedDiffHunk(line) {
    return /^@{2,3} .* @{2,3}(?: .*)?$/.test(line);
}
function isDiffMetadata(line) {
    return /^(?:index |old mode |new mode |deleted file mode |new file mode |similarity index |dissimilarity index |rename from |rename to |copy from |copy to |--- |\+\+\+ |Binary files |GIT binary patch$|literal \d+$|delta \d+$)/.test(line);
}
function findUnfencedDiffEnd(lines, start) {
    let index = start;
    let sawHunk = false;
    while (index < lines.length) {
        const line = lines[index];
        if (isGitDiffHeader(line) || isDiffMetadata(line)) {
            index++;
            continue;
        }
        if (isUnifiedDiffHunk(line)) {
            sawHunk = true;
            index++;
            continue;
        }
        if (sawHunk && /^[ +\\-]/.test(line)) {
            index++;
            continue;
        }
        if (sawHunk && /^\\ No newline at end of file$/.test(line)) {
            index++;
            continue;
        }
        if (line === '') {
            let next = index + 1;
            while (next < lines.length && lines[next] === '')
                next++;
            if (next < lines.length && isGitDiffHeader(lines[next])) {
                index = next;
                continue;
            }
        }
        break;
    }
    return Math.max(index, start + 1);
}
function technicalMarkupRoot(lines, index) {
    const trimmed = (lines[index] ?? '').trimStart();
    if (/^<\?xml\b/i.test(trimmed) && /^<svg\b/i.test((lines[index + 1] ?? '').trimStart()))
        return 'svg';
    if (/^<!doctype\s+html\b/i.test(trimmed))
        return 'html';
    const match = /^<(svg|html|head|body|main|header|footer|section|article|aside|nav|div|table|thead|tbody|tfoot|tr|ul|ol|li|style|script|template|form|figure|picture|details|summary|pre|h[1-6])(?:\s|>|\/)/i.exec(trimmed);
    return match ? match[1].toLowerCase() : null;
}
function findTechnicalMarkupEnd(lines, start, root) {
    const closing = new RegExp('</' + root + '\\s*>', 'i');
    const searchLimit = Math.min(lines.length, start + 500);
    for (let index = start; index < searchLimit; index++) {
        if (closing.test(lines[index]))
            return index + 1;
    }
    let end = start + 1;
    while (end < lines.length
        && lines[end] !== ''
        && (/^\s+</.test(lines[end]) || /^\s{2,}\S/.test(lines[end]))) {
        end++;
    }
    return end;
}
function appendTechnicalFence(target, source, language) {
    const tick = String.fromCharCode(96);
    const runs = source.join('\n').match(/\x60+/g) ?? [];
    const longest = runs.reduce((max, run) => Math.max(max, run.length), 0);
    const fence = tick.repeat(Math.max(3, longest + 1));
    target.push(fence + language, ...source, fence);
}`;

const markdownPath = join(packageRoot, 'fesm2022', 'coding-agent-chat-markdown.mjs');
let markdown = readFileSync(markdownPath, 'utf8');

markdown = replaceExact(
  markdown,
  `const MAX_HIGHLIGHT_CHARS = 60_000;\nfunction markdownToHtml(markdown, options = {}) {`,
  `const MAX_HIGHLIGHT_CHARS = 60_000;\n${technicalProtectionSource}\nfunction markdownToHtml(markdown, options = {}) {`,
  1,
  'technical block protector',
);

markdown = replaceExact(
  markdown,
  `        const parsed = local.parse(markdown);`,
  `        const parsed = local.parse(protectTechnicalMarkdown(markdown));`,
  1,
  'markdown preprocessing call',
);

markdown = replaceExact(
  markdown,
  `                const lang = (token.lang ?? '').trim().split(/\\s+/, 1)[0]?.toLowerCase() || null;\n                return renderCodeBlock(token.text ?? '', lang, options);`,
  `                const detectedLang = (token.lang ?? '').trim().split(/\\s+/, 1)[0]?.toLowerCase() || null;\n                const lang = detectedLang === 'diff-autodetected' ? 'diff'\n                    : detectedLang === 'html-autodetected' ? 'html'\n                        : detectedLang;\n                return renderCodeBlock(token.text ?? '', lang, options);`,
  1,
  'autodetected language normalization',
);

markdown = replaceExact(
  markdown,
  `, markdownToHtml, sanitizeHtml };`,
  `, markdownToHtml, protectTechnicalMarkdown, sanitizeHtml };`,
  1,
  'technical block protector export',
);

writeFileSync(markdownPath, markdown);

const markdownTypesPath = join(packageRoot, 'types', 'coding-agent-chat-markdown.d.ts');
let markdownTypes = readFileSync(markdownTypesPath, 'utf8');
markdownTypes = replaceExact(
  markdownTypes,
  `declare function markdownToHtml(markdown: string, options?: MarkdownImageOptions): string;`,
  `declare function protectTechnicalMarkdown(markdown: string): string;\ndeclare function markdownToHtml(markdown: string, options?: MarkdownImageOptions): string;`,
  1,
  'technical block protector type',
);
markdownTypes = replaceExact(
  markdownTypes,
  `, markdownToHtml, sanitizeHtml };`,
  `, markdownToHtml, protectTechnicalMarkdown, sanitizeHtml };`,
  1,
  'technical block protector type export',
);
writeFileSync(markdownTypesPath, markdownTypes);
