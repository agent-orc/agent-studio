import { afterAll, afterEach, beforeEach, expect } from 'vitest';

// These DOM slots are replaced by component specs. With isolate: false, a
// leaked descriptor can break an unrelated spec assigned to the same worker.
// Stub the instance, or restore the original descriptor in afterEach.
const monitored: readonly { owner: object; key: PropertyKey; name: string }[] = [
  { owner: Element.prototype, key: 'scrollIntoView', name: 'Element.prototype.scrollIntoView' },
  { owner: HTMLElement.prototype, key: 'scrollIntoView', name: 'HTMLElement.prototype.scrollIntoView' },
  { owner: HTMLCanvasElement.prototype, key: 'getContext', name: 'HTMLCanvasElement.prototype.getContext' },
  { owner: HTMLCanvasElement.prototype, key: 'toBlob', name: 'HTMLCanvasElement.prototype.toBlob' },
  { owner: navigator, key: 'clipboard', name: 'navigator.clipboard' },
];

type Snapshot = (PropertyDescriptor | undefined)[];

const snapshot = (): Snapshot => monitored.map(({ owner, key }) => Object.getOwnPropertyDescriptor(owner, key));

const sameDescriptor = (a: PropertyDescriptor | undefined, b: PropertyDescriptor | undefined): boolean =>
  a?.configurable === b?.configurable
  && a?.enumerable === b?.enumerable
  && a?.writable === b?.writable
  && Object.is(a?.value, b?.value)
  && Object.is(a?.get, b?.get)
  && Object.is(a?.set, b?.set);

/**
 * Restores every changed slot before reporting, so one leaking spec cannot
 * poison the specs that run after it in the same worker. Returns the names of
 * the restored slots.
 */
function restoreChanged(original: Snapshot): string[] {
  const leaked: string[] = [];
  for (const [index, { owner, key, name }] of monitored.entries()) {
    if (sameDescriptor(original[index], Object.getOwnPropertyDescriptor(owner, key))) continue;
    leaked.push(name);
    const descriptor = original[index];
    if (descriptor) Object.defineProperty(owner, key, descriptor);
    else Reflect.deleteProperty(owner, key);
  }
  return leaked;
}

// Setup files run before each spec file is imported, so this also sees
// replacements a spec makes at module level.
const fileDescriptors = snapshot();
let testDescriptors: Snapshot;

beforeEach(() => {
  testDescriptors = snapshot();
});

afterEach(() => {
  const leaked = restoreChanged(testDescriptors);
  if (leaked.length > 0) {
    throw new Error(`${expect.getState().currentTestName}: leaked ${leaked.join(', ')} property descriptor`
      + `${leaked.length > 1 ? 's' : ''}; restore the original in afterEach or stub the instance`);
  }
});

// Catches replacements made at module level or in beforeAll that outlive the file.
afterAll(() => {
  const leaked = restoreChanged(fileDescriptors);
  if (leaked.length > 0) {
    throw new Error(`${expect.getState().testPath ?? 'spec file'}: leaked ${leaked.join(', ')} property descriptor`
      + `${leaked.length > 1 ? 's' : ''} past its last test; restore the original in afterAll`);
  }
});
