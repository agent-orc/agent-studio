#!/usr/bin/env node
// Visual-design capture: screenshots at desktop and phone width in light and
// dark, plus a technical report (console errors, page errors, failed requests,
// horizontal overflow, frame timing, optional game text state).
//
// A local file or folder target is served over loopback HTTP so ES modules and
// fetch() work the way they do in a browser. Exit codes: 0 clean, 1 usage or
// setup error, 3 the capture found technical issues.
//
// See ../SKILL.md (step 5) and ../references/games.md#test-hooks.

import { createServer } from 'node:http';
import { createReadStream, existsSync, mkdirSync, statSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { dirname, extname, join, normalize, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const VIEWPORTS = {
  desktop: { width: 1440, height: 900, deviceScaleFactor: 1, isMobile: false, hasTouch: false },
  phone: { width: 390, height: 844, deviceScaleFactor: 2, isMobile: true, hasTouch: true },
};

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.gif': 'image/gif',
  '.ico': 'image/x-icon',
  '.woff2': 'font/woff2',
  '.woff': 'font/woff',
  '.ttf': 'font/ttf',
  '.otf': 'font/otf',
  '.glb': 'model/gltf-binary',
  '.gltf': 'model/gltf+json',
  '.hdr': 'application/octet-stream',
  '.ktx2': 'image/ktx2',
  '.wasm': 'application/wasm',
  '.mp3': 'audio/mpeg',
  '.ogg': 'audio/ogg',
  '.wav': 'audio/wav',
  '.txt': 'text/plain; charset=utf-8',
};

const HELP = `Usage: node capture.mjs --target <url|file|folder> [options]

Options:
  --target <t>          URL, local HTML file, or folder with index.html (required)
  --out <dir>           Output folder. Default: $JOB_RESULTS_DIR/visual-review,
                        else ./visual-review
  --name <prefix>       File name prefix, for example round1. Default: capture
  --viewports <list>    desktop,phone (default) or WIDTHxHEIGHT entries
  --themes <list>       light,dark (default)
  --theme-attribute <a> Also set <html a="light|dark"> after load, for pages
                        that switch theme by attribute instead of media query
  --wait-ms <n>         Settle time after load before the first shot. Default: 800
  --full-page           Capture the full scrollable page, not only the viewport
  --click <selector>    Click this element after the first shot
  --keys <list>         Comma-separated keys to press after the first shot,
                        for example "Enter,ArrowRight,Space"
  --advance-ms <n>      Advance time after input: calls window.advanceTime(n)
                        when the page defines it, else waits n ms
  --frame-sample-ms <n> Length of the requestAnimationFrame sample. Default: 1500
  --headed              Show the browser (helps when WebGL renders black)
  --help                Show this text

When --click, --keys, or --advance-ms is given, a second shot named
<name>-<viewport>-<theme>-after.png shows the state after input.`;

const KNOWN_FLAGS = new Set([
  '--target', '--out', '--name', '--viewports', '--themes', '--theme-attribute', '--wait-ms', '--full-page',
  '--click', '--keys', '--advance-ms', '--frame-sample-ms', '--headed', '--help', '-h',
]);

export function parseArgs(argv) {
  const options = {
    target: null,
    out: null,
    name: 'capture',
    viewports: ['desktop', 'phone'],
    themes: ['light', 'dark'],
    themeAttribute: null,
    waitMs: 800,
    fullPage: false,
    click: null,
    keys: [],
    advanceMs: null,
    frameSampleMs: 1500,
    headed: false,
    help: false,
  };
  const list = (value) => value.split(',').map((item) => item.trim()).filter(Boolean);
  const number = (flag, value) => {
    const parsed = Number(value);
    if (!Number.isFinite(parsed) || parsed < 0) throw new Error(`${flag} needs a non-negative number, got '${value}'`);
    return parsed;
  };
  for (let i = 0; i < argv.length; i++) {
    const flag = argv[i];
    if (!KNOWN_FLAGS.has(flag)) throw new Error(`Unknown option '${flag}'`);
    const takesValue = !['--full-page', '--headed', '--help', '-h'].includes(flag);
    const value = takesValue ? argv[++i] : undefined;
    if (takesValue && (value === undefined || value.startsWith('--'))) throw new Error(`${flag} needs a value`);
    switch (flag) {
      case '--target': options.target = value; break;
      case '--out': options.out = value; break;
      case '--name': options.name = value; break;
      case '--viewports': options.viewports = list(value); break;
      case '--themes': options.themes = list(value); break;
      case '--theme-attribute': options.themeAttribute = value; break;
      case '--wait-ms': options.waitMs = number(flag, value); break;
      case '--full-page': options.fullPage = true; break;
      case '--click': options.click = value; break;
      case '--keys': options.keys = list(value); break;
      case '--advance-ms': options.advanceMs = number(flag, value); break;
      case '--frame-sample-ms': options.frameSampleMs = number(flag, value); break;
      case '--headed': options.headed = true; break;
      case '--help': case '-h': options.help = true; break;
    }
  }
  if (!options.help && !options.target) throw new Error('--target is required');
  for (const theme of options.themes) {
    if (theme !== 'light' && theme !== 'dark') throw new Error(`Unknown theme '${theme}', use light or dark`);
  }
  for (const viewport of options.viewports) viewportSpec(viewport);
  if (!/^[\w.-]+$/.test(options.name)) throw new Error(`--name may only contain letters, digits, '.', '_' and '-'`);
  return options;
}

export function viewportSpec(name) {
  if (VIEWPORTS[name]) return VIEWPORTS[name];
  const match = /^(\d{2,5})x(\d{2,5})$/.exec(name);
  if (!match) throw new Error(`Unknown viewport '${name}', use desktop, phone, or WIDTHxHEIGHT`);
  const width = Number(match[1]);
  const height = Number(match[2]);
  const narrow = width < 768;
  return { width, height, deviceScaleFactor: narrow ? 2 : 1, isMobile: narrow, hasTouch: narrow };
}

export function frameStats(deltas) {
  const frames = deltas.filter((delta) => delta > 0).sort((a, b) => a - b);
  if (frames.length === 0) return { frames: 0, medianMs: null, p95Ms: null, maxMs: null, longFrames: 0 };
  const pick = (q) => frames[Math.min(frames.length - 1, Math.floor(q * frames.length))];
  const round = (value) => Math.round(value * 10) / 10;
  return {
    frames: frames.length,
    medianMs: round(pick(0.5)),
    p95Ms: round(pick(0.95)),
    maxMs: round(frames[frames.length - 1]),
    longFrames: frames.filter((delta) => delta > 50).length,
  };
}

async function loadChromium() {
  const scriptDir = dirname(fileURLToPath(import.meta.url));
  const bases = [process.cwd()];
  const gitRoot = (dir) => {
    try {
      return execFileSync('git', ['rev-parse', '--show-toplevel'], { cwd: dir, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
    } catch {
      return null;
    }
  };
  for (const root of [gitRoot(process.cwd()), gitRoot(scriptDir)]) {
    if (root) bases.push(root, join(root, 'frontend'));
  }
  try {
    bases.push(execFileSync('npm', ['root', '-g'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim());
  } catch {
    // No global npm root; the local bases may still resolve.
  }
  for (const base of bases) {
    const require = createRequire(join(base, 'noop.js'));
    for (const name of ['playwright', '@playwright/test', 'playwright-core']) {
      try {
        const module = await import(pathToFileURL(require.resolve(name)).href);
        const chromium = module.chromium ?? module.default?.chromium;
        if (chromium) return { chromium, from: dirname(require.resolve(name)) };
      } catch {
        // Try the next package name or base.
      }
    }
  }
  throw new Error(
    'Playwright not found. Install it next to the work (npm i -D playwright && npx playwright install chromium) ' +
    'or run from a repository whose frontend/ has it.');
}

function serveFolder(root, entry) {
  const base = resolve(root);
  const server = createServer((request, response) => {
    const path = decodeURIComponent(new URL(request.url, 'http://127.0.0.1').pathname);
    let file = normalize(join(base, path));
    if (file !== base && !file.startsWith(base + sep)) {
      response.writeHead(403).end();
      return;
    }
    if (existsSync(file) && statSync(file).isDirectory()) file = join(file, 'index.html');
    if (!existsSync(file)) {
      response.writeHead(404).end();
      return;
    }
    response.writeHead(200, {
      'content-type': MIME[extname(file).toLowerCase()] ?? 'application/octet-stream',
      'cache-control': 'no-store',
    });
    createReadStream(file).pipe(response);
  });
  return new Promise((resolveServer, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      resolveServer({ server, url: `http://127.0.0.1:${port}/${entry}` });
    });
  });
}

async function resolveTarget(target) {
  if (/^(https?|file):\/\//.test(target)) return { url: target, server: null };
  const path = resolve(target);
  if (!existsSync(path)) throw new Error(`Target '${target}' is neither a URL nor an existing path`);
  if (statSync(path).isDirectory()) {
    if (!existsSync(join(path, 'index.html'))) throw new Error(`Folder '${target}' has no index.html`);
    return serveFolder(path, '');
  }
  return serveFolder(dirname(path), encodeURIComponent(path.split(sep).pop()));
}

async function sampleFrames(page, durationMs) {
  if (durationMs <= 0) return frameStats([]);
  const deltas = await page.evaluate((ms) => new Promise((done) => {
    const out = [];
    let last = performance.now();
    const end = last + ms;
    const tick = (now) => {
      out.push(now - last);
      last = now;
      if (now < end) requestAnimationFrame(tick); else done(out);
    };
    requestAnimationFrame(tick);
  }), durationMs);
  return frameStats(deltas);
}

// Measured against the emulated viewport width, not window.innerWidth: mobile
// emulation widens innerWidth to the content when a page overflows.
async function measureLayout(page, viewportWidth) {
  const layout = await page.evaluate(() => {
    const doc = document.documentElement;
    return {
      scrollWidth: Math.max(doc.scrollWidth, document.body?.scrollWidth ?? 0),
      fontsLoaded: document.fonts ? [...document.fonts].filter((f) => f.status === 'loaded').map((f) => `${f.family} ${f.weight}`) : [],
    };
  });
  return { viewportWidth, ...layout, horizontalOverflowPx: Math.max(0, layout.scrollWidth - viewportWidth) };
}

async function readGameState(page) {
  return page.evaluate(() => {
    if (typeof window.render_game_to_text !== 'function') return null;
    try {
      return String(window.render_game_to_text()).slice(0, 4000);
    } catch (error) {
      return `render_game_to_text threw: ${error}`;
    }
  });
}

async function applyInput(page, options) {
  if (options.click) await page.click(options.click, { timeout: 5000 });
  for (const key of options.keys) {
    await page.keyboard.press(key);
    await page.waitForTimeout(60);
  }
  if (options.advanceMs !== null) {
    const advanced = await page.evaluate((ms) => {
      if (typeof window.advanceTime !== 'function') return false;
      window.advanceTime(ms);
      return true;
    }, options.advanceMs);
    if (!advanced) await page.waitForTimeout(options.advanceMs);
  }
  await page.waitForTimeout(150);
}

async function captureOne(browser, url, viewportName, theme, options, outDir) {
  const spec = viewportSpec(viewportName);
  const context = await browser.newContext({
    viewport: { width: spec.width, height: spec.height },
    deviceScaleFactor: spec.deviceScaleFactor,
    isMobile: spec.isMobile,
    hasTouch: spec.hasTouch,
    colorScheme: theme,
  });
  const page = await context.newPage();
  const consoleErrors = [];
  const pageErrors = [];
  const failedRequests = [];
  // Chromium asks for /favicon.ico on its own; a missing one is not a defect of the work.
  const isFavicon = (url) => /\/favicon\.ico(\?|$)/.test(url ?? '');
  page.on('console', (message) => {
    if (message.type() === 'error' && !isFavicon(message.location()?.url)) consoleErrors.push(message.text().slice(0, 500));
  });
  page.on('pageerror', (error) => pageErrors.push(String(error).slice(0, 500)));
  page.on('requestfailed', (request) => {
    if (!isFavicon(request.url())) failedRequests.push(`${request.url()} (${request.failure()?.errorText ?? 'failed'})`);
  });
  page.on('response', (response) => {
    if (response.status() >= 400 && !isFavicon(response.url())) failedRequests.push(`${response.url()} (HTTP ${response.status()})`);
  });

  const base = `${options.name}-${viewportName}-${theme}`;
  const shot = { viewport: viewportName, theme, size: `${spec.width}x${spec.height}`, screenshots: [] };
  try {
    await page.goto(url, { waitUntil: 'load', timeout: 30000 });
    if (options.themeAttribute) {
      await page.evaluate(([name, value]) => document.documentElement.setAttribute(name, value), [options.themeAttribute, theme]);
    }
    await page.waitForTimeout(options.waitMs);
    const first = join(outDir, `${base}.png`);
    await page.screenshot({ path: first, fullPage: options.fullPage });
    shot.screenshots.push(first);
    shot.layout = await measureLayout(page, spec.width);
    shot.frames = await sampleFrames(page, options.frameSampleMs);
    shot.gameState = await readGameState(page);

    if (options.click || options.keys.length > 0 || options.advanceMs !== null) {
      await applyInput(page, options);
      const after = join(outDir, `${base}-after.png`);
      await page.screenshot({ path: after, fullPage: options.fullPage });
      shot.screenshots.push(after);
      shot.layoutAfter = await measureLayout(page, spec.width);
      shot.framesAfter = await sampleFrames(page, options.frameSampleMs);
      shot.gameStateAfter = await readGameState(page);
    }
  } catch (error) {
    pageErrors.push(`capture failed: ${String(error).split('\n')[0]}`);
  } finally {
    await context.close();
  }
  shot.consoleErrors = consoleErrors;
  shot.pageErrors = pageErrors;
  shot.failedRequests = failedRequests;
  shot.issues = [
    ...consoleErrors.map((text) => `console error: ${text}`),
    ...pageErrors.map((text) => `page error: ${text}`),
    ...failedRequests.map((text) => `failed request: ${text}`),
    ...[shot.layout, shot.layoutAfter]
      .filter((layout) => layout && layout.horizontalOverflowPx > 0)
      .map((layout) => `horizontal overflow: ${layout.horizontalOverflowPx}px beyond ${layout.viewportWidth}px viewport`),
  ];
  return shot;
}

async function main() {
  let options;
  try {
    options = parseArgs(process.argv.slice(2));
  } catch (error) {
    console.error(`capture: ${error.message}\n\n${HELP}`);
    return 1;
  }
  if (options.help) {
    console.log(HELP);
    return 0;
  }

  const outDir = resolve(options.out
    ?? (process.env.JOB_RESULTS_DIR ? join(process.env.JOB_RESULTS_DIR, 'visual-review') : 'visual-review'));
  mkdirSync(outDir, { recursive: true });

  let chromium;
  let target;
  try {
    ({ chromium } = await loadChromium());
    target = await resolveTarget(options.target);
  } catch (error) {
    console.error(`capture: ${error.message}`);
    return 1;
  }

  const browser = await chromium.launch({
    headless: !options.headed,
    // Software WebGL in headless Chromium, so 3D scenes and games render.
    args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader', '--ignore-gpu-blocklist'],
  });
  const shots = [];
  try {
    for (const viewport of options.viewports) {
      for (const theme of options.themes) {
        shots.push(await captureOne(browser, target.url, viewport, theme, options, outDir));
      }
    }
  } finally {
    await browser.close();
    target.server?.close();
  }

  const report = {
    target: options.target,
    url: target.url,
    capturedAt: new Date().toISOString(),
    note: 'Frame timings come from headless Chromium with software WebGL; compare them between rounds, not with real devices.',
    shots,
    issueCount: shots.reduce((sum, shot) => sum + shot.issues.length, 0),
  };
  const reportPath = join(outDir, `${options.name}-capture.json`);
  writeFileSync(reportPath, `${JSON.stringify(report, null, 2)}\n`);

  for (const shot of shots) {
    const frames = shot.frames?.medianMs == null ? 'no frames' : `median ${shot.frames.medianMs} ms, p95 ${shot.frames.p95Ms} ms`;
    console.log(`${shot.viewport} ${shot.theme}: ${shot.screenshots.length} shot(s), ${frames}, ${shot.issues.length} issue(s)`);
    for (const issue of shot.issues) console.log(`  - ${issue}`);
  }
  console.log(`Screenshots and report: ${outDir}`);
  console.log(`Report: ${reportPath}`);
  return report.issueCount > 0 ? 3 : 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().then((code) => { process.exitCode = code; }, (error) => {
    console.error(`capture: ${error.stack ?? error}`);
    process.exitCode = 1;
  });
}
