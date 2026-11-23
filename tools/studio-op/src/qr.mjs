/**
 * A small QR code encoder (ISO/IEC 18004) for `studio-op login`: byte mode,
 * error correction level M, versions 1 to 10, enough for a confirmation link
 * of about 200 characters. studio-op has no dependencies, so the encoder
 * lives here. It follows the structure of Project Nayuki's reference
 * implementation; the test compares its modules with the `qrcode` package.
 */

// Level M, versions 1 to 10: error correction codewords per block and number of blocks.
const ECC_PER_BLOCK = [10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
const BLOCKS = [1, 1, 1, 2, 2, 4, 4, 4, 5, 5];
const FORMAT_LEVEL_M = 0;
export const MAX_VERSION = 10;

const bit = (value, index) => ((value >>> index) & 1) !== 0;

function rawDataModules(version) {
  let result = (16 * version + 128) * version + 64;
  if (version >= 2) {
    const align = Math.floor(version / 7) + 2;
    result -= (25 * align - 10) * align - 55;
    if (version >= 7) result -= 36;
  }
  return result;
}

const dataCodewords = version => Math.floor(rawDataModules(version) / 8) - ECC_PER_BLOCK[version - 1] * BLOCKS[version - 1];

/** Bytes per version, after the mode indicator and the character count. */
export function capacity(version) {
  const countBits = version < 10 ? 8 : 16;
  return Math.floor((dataCodewords(version) * 8 - 4 - countBits) / 8);
}

function gfMultiply(x, y) {
  let z = 0;
  for (let i = 7; i >= 0; i--) {
    z = (z << 1) ^ ((z >>> 7) * 0x11d);
    z ^= ((y >>> i) & 1) * x;
  }
  return z;
}

function rsDivisor(degree) {
  const result = new Array(degree).fill(0);
  result[degree - 1] = 1;
  let root = 1;
  for (let i = 0; i < degree; i++) {
    for (let j = 0; j < result.length; j++) {
      result[j] = gfMultiply(result[j], root);
      if (j + 1 < result.length) result[j] ^= result[j + 1];
    }
    root = gfMultiply(root, 0x02);
  }
  return result;
}

function rsRemainder(data, divisor) {
  const result = divisor.map(() => 0);
  for (const byte of data) {
    const factor = byte ^ result.shift();
    result.push(0);
    divisor.forEach((coefficient, index) => { result[index] ^= gfMultiply(coefficient, factor); });
  }
  return result;
}

function encodeData(bytes, version) {
  const bits = [];
  const push = (value, length) => { for (let i = length - 1; i >= 0; i--) bits.push((value >>> i) & 1); };
  push(0b0100, 4);
  push(bytes.length, version < 10 ? 8 : 16);
  for (const byte of bytes) push(byte, 8);
  const capacityBits = dataCodewords(version) * 8;
  push(0, Math.min(4, capacityBits - bits.length));
  push(0, (8 - bits.length % 8) % 8);
  for (let pad = 0xec; bits.length < capacityBits; pad ^= 0xec ^ 0x11) push(pad, 8);
  const codewords = [];
  for (let i = 0; i < bits.length; i += 8) codewords.push(bits.slice(i, i + 8).reduce((acc, value) => (acc << 1) | value, 0));
  return codewords;
}

function withErrorCorrection(data, version) {
  const blocks = BLOCKS[version - 1];
  const eccLength = ECC_PER_BLOCK[version - 1];
  const raw = Math.floor(rawDataModules(version) / 8);
  const shortBlocks = blocks - raw % blocks;
  const shortLength = Math.floor(raw / blocks);
  const divisor = rsDivisor(eccLength);
  const split = [];
  for (let i = 0, offset = 0; i < blocks; i++) {
    const length = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
    const block = data.slice(offset, offset + length);
    offset += length;
    const ecc = rsRemainder(block, divisor);
    if (i < shortBlocks) block.push(0);
    split.push([...block, ...ecc]);
  }
  const result = [];
  for (let i = 0; i < split[0].length; i++)
    split.forEach((block, index) => {
      if (i !== shortLength - eccLength || index >= shortBlocks) result.push(block[i]);
    });
  return result;
}

function alignmentPositions(version) {
  if (version === 1) return [];
  const count = Math.floor(version / 7) + 2;
  const step = Math.ceil((version * 4 + 4) / (count * 2 - 2)) * 2;
  const result = [6];
  for (let position = version * 4 + 10; result.length < count; position -= step) result.splice(1, 0, position);
  return result;
}

const MASKS = [
  (x, y) => (x + y) % 2 === 0,
  (_x, y) => y % 2 === 0,
  x => x % 3 === 0,
  (x, y) => (x + y) % 3 === 0,
  (x, y) => (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0,
  (x, y) => (x * y) % 2 + (x * y) % 3 === 0,
  (x, y) => ((x * y) % 2 + (x * y) % 3) % 2 === 0,
  (x, y) => ((x + y) % 2 + (x * y) % 3) % 2 === 0,
];

class Matrix {
  constructor(version) {
    this.version = version;
    this.size = version * 4 + 17;
    this.modules = Array.from({ length: this.size }, () => new Array(this.size).fill(false));
    this.reserved = Array.from({ length: this.size }, () => new Array(this.size).fill(false));
  }

  set(x, y, dark) {
    this.modules[y][x] = dark;
    this.reserved[y][x] = true;
  }

  drawFunctionPatterns() {
    const { size } = this;
    for (let i = 0; i < size; i++) {
      this.set(6, i, i % 2 === 0);
      this.set(i, 6, i % 2 === 0);
    }
    for (const [cx, cy] of [[3, 3], [size - 4, 3], [3, size - 4]])
      for (let dy = -4; dy <= 4; dy++)
        for (let dx = -4; dx <= 4; dx++) {
          const x = cx + dx, y = cy + dy;
          const distance = Math.max(Math.abs(dx), Math.abs(dy));
          if (x >= 0 && x < size && y >= 0 && y < size) this.set(x, y, distance !== 2 && distance !== 4);
        }
    const positions = alignmentPositions(this.version);
    const last = positions.length - 1;
    positions.forEach((x, i) => positions.forEach((y, j) => {
      if ((i === 0 && j === 0) || (i === 0 && j === last) || (i === last && j === 0)) return;
      for (let dy = -2; dy <= 2; dy++)
        for (let dx = -2; dx <= 2; dx++) this.set(x + dx, y + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
    }));
    this.drawFormat(0);
    this.drawVersion();
  }

  drawFormat(mask) {
    const data = (FORMAT_LEVEL_M << 3) | mask;
    let remainder = data;
    for (let i = 0; i < 10; i++) remainder = (remainder << 1) ^ ((remainder >>> 9) * 0x537);
    const bits = ((data << 10) | remainder) ^ 0x5412;
    const { size } = this;
    for (let i = 0; i <= 5; i++) this.set(8, i, bit(bits, i));
    this.set(8, 7, bit(bits, 6));
    this.set(8, 8, bit(bits, 7));
    this.set(7, 8, bit(bits, 8));
    for (let i = 9; i < 15; i++) this.set(14 - i, 8, bit(bits, i));
    for (let i = 0; i < 8; i++) this.set(size - 1 - i, 8, bit(bits, i));
    for (let i = 8; i < 15; i++) this.set(8, size - 15 + i, bit(bits, i));
    this.set(8, size - 8, true);
  }

  drawVersion() {
    if (this.version < 7) return;
    let remainder = this.version;
    for (let i = 0; i < 12; i++) remainder = (remainder << 1) ^ ((remainder >>> 11) * 0x1f25);
    const bits = (this.version << 12) | remainder;
    for (let i = 0; i < 18; i++) {
      const a = this.size - 11 + (i % 3);
      const b = Math.floor(i / 3);
      this.set(a, b, bit(bits, i));
      this.set(b, a, bit(bits, i));
    }
  }

  drawCodewords(codewords) {
    const { size } = this;
    let index = 0;
    for (let right = size - 1; right >= 1; right -= 2) {
      if (right === 6) right = 5;
      for (let vertical = 0; vertical < size; vertical++)
        for (let j = 0; j < 2; j++) {
          const x = right - j;
          const upward = ((right + 1) & 2) === 0;
          const y = upward ? size - 1 - vertical : vertical;
          if (!this.reserved[y][x] && index < codewords.length * 8) {
            this.modules[y][x] = bit(codewords[index >>> 3], 7 - (index & 7));
            index++;
          }
        }
    }
  }

  applyMask(mask) {
    const test = MASKS[mask];
    for (let y = 0; y < this.size; y++)
      for (let x = 0; x < this.size; x++)
        if (!this.reserved[y][x] && test(x, y)) this.modules[y][x] = !this.modules[y][x];
  }

  /** The ISO penalty: long runs, 2x2 blocks, finder-like patterns and the dark balance. */
  penalty() {
    const { size, modules } = this;
    let score = 0;
    const lines = [];
    for (let i = 0; i < size; i++) {
      lines.push(modules[i]);
      lines.push(modules.map(row => row[i]));
    }
    for (const line of lines) {
      let run = 1;
      for (let i = 1; i <= size; i++) {
        if (i < size && line[i] === line[i - 1]) { run++; continue; }
        if (run >= 5) score += 3 + run - 5;
        run = 1;
      }
      const text = line.map(dark => (dark ? '1' : '0')).join('');
      for (const pattern of ['10111010000', '00001011101'])
        for (let at = text.indexOf(pattern); at >= 0; at = text.indexOf(pattern, at + 1)) score += 40;
    }
    for (let y = 0; y + 1 < size; y++)
      for (let x = 0; x + 1 < size; x++) {
        const dark = modules[y][x];
        if (dark === modules[y][x + 1] && dark === modules[y + 1][x] && dark === modules[y + 1][x + 1]) score += 3;
      }
    const darkCount = modules.reduce((sum, row) => sum + row.filter(Boolean).length, 0);
    score += Math.floor(Math.abs(darkCount * 100 / (size * size) - 50) / 5) * 10;
    return score;
  }
}

/**
 * Encodes text as a QR code at level M. Returns the module rows (true is
 * dark), the version and the mask. `mask` forces one of the eight masks;
 * otherwise the one with the lowest penalty is used.
 */
export function encodeQr(text, { mask = null, version: forced = null } = {}) {
  const bytes = [...Buffer.from(String(text), 'utf8')];
  let version = forced ?? 1;
  while (version <= MAX_VERSION && capacity(version) < bytes.length) {
    if (forced) throw new RangeError(`The text does not fit version ${forced}.`);
    version++;
  }
  if (version > MAX_VERSION) throw new RangeError(`The text is longer than ${capacity(MAX_VERSION)} bytes.`);
  const codewords = withErrorCorrection(encodeData(bytes, version), version);
  const build = candidate => {
    const matrix = new Matrix(version);
    matrix.drawFunctionPatterns();
    matrix.drawCodewords(codewords);
    matrix.applyMask(candidate);
    matrix.drawFormat(candidate);
    return matrix;
  };
  let best = null;
  for (const candidate of mask === null ? [0, 1, 2, 3, 4, 5, 6, 7] : [mask]) {
    const matrix = build(candidate);
    const score = mask === null ? matrix.penalty() : 0;
    if (!best || score < best.score) best = { matrix, score, mask: candidate };
  }
  return { modules: best.matrix.modules, size: best.matrix.size, version, mask: best.mask };
}

/**
 * Renders a QR code for a terminal with half blocks: two module rows per
 * text line and a quiet zone of two modules. Light modules print as block
 * characters in the terminal's text colour and dark modules as its
 * background, as other terminal QR renderers do; on a light theme the code
 * appears inverted, which phone cameras read as well.
 */
export function renderQr(text, { quiet = 2 } = {}) {
  const { modules, size } = encodeQr(text);
  const dark = (x, y) => x >= 0 && y >= 0 && x < size && y < size && modules[y][x];
  const lines = [];
  for (let y = -quiet; y < size + quiet; y += 2) {
    let line = '';
    for (let x = -quiet; x < size + quiet; x++) {
      const top = dark(x, y);
      const bottom = dark(x, y + 1);
      // Light modules are full blocks (light on a dark terminal), dark ones are spaces.
      line += top && bottom ? ' ' : top ? '▄' : bottom ? '▀' : '█';
    }
    lines.push(line);
  }
  return lines.join('\n');
}
