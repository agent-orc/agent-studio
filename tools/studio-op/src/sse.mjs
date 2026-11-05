/**
 * Minimal Server-Sent Events parser for the operator decision stream. Feed it
 * text chunks in arrival order; it calls `onFrame({ id, event, data })` for
 * every complete frame that carries data. Comments (keep-alives) and the
 * `retry` field are ignored.
 */
export class SseParser {
  constructor(onFrame) {
    this.onFrame = onFrame;
    this.buffer = '';
    this.reset();
  }

  reset() {
    this.id = null;
    this.event = null;
    this.data = [];
  }

  push(chunk) {
    this.buffer += chunk;
    let newline;
    while ((newline = this.buffer.search(/\r\n|\r|\n/)) >= 0) {
      const line = this.buffer.slice(0, newline);
      const width = this.buffer.startsWith('\r\n', newline) ? 2 : 1;
      // A lone \r at the end of a chunk may be the first half of \r\n.
      if (width === 1 && this.buffer[newline] === '\r' && newline === this.buffer.length - 1) return;
      this.buffer = this.buffer.slice(newline + width);
      this.line(line);
    }
  }

  line(line) {
    if (line === '') {
      if (this.data.length > 0)
        this.onFrame({ id: this.id, event: this.event ?? 'message', data: this.data.join('\n') });
      this.reset();
      return;
    }
    if (line.startsWith(':')) return;
    const colon = line.indexOf(':');
    const field = colon < 0 ? line : line.slice(0, colon);
    let value = colon < 0 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) value = value.slice(1);
    if (field === 'id') this.id = value;
    else if (field === 'event') this.event = value;
    else if (field === 'data') this.data.push(value);
  }
}
