import { afterEach, beforeEach, expect } from 'vitest';

// These prototype slots are replaced by component specs. With isolate: false,
// a leaked descriptor can break an unrelated spec assigned to the same worker.
const monitored = [
  { owner: Element.prototype, name: 'Element.prototype.scrollIntoView' },
  { owner: HTMLElement.prototype, name: 'HTMLElement.prototype.scrollIntoView' },
] as const;

let descriptors: (PropertyDescriptor | undefined)[];

beforeEach(() => {
  descriptors = monitored.map(({ owner }) => Object.getOwnPropertyDescriptor(owner, 'scrollIntoView'));
});

afterEach(() => {
  for (const [index, { owner, name }] of monitored.entries()) {
    const original = descriptors[index];
    const current = Object.getOwnPropertyDescriptor(owner, 'scrollIntoView');
    const unchanged = original?.configurable === current?.configurable
      && original?.enumerable === current?.enumerable
      && original?.writable === current?.writable
      && Object.is(original?.value, current?.value)
      && Object.is(original?.get, current?.get)
      && Object.is(original?.set, current?.set);
    if (unchanged) continue;

    // Restore before reporting so one leaking spec cannot poison later specs.
    if (original) Object.defineProperty(owner, 'scrollIntoView', original);
    else Reflect.deleteProperty(owner, 'scrollIntoView');
    throw new Error(`${expect.getState().currentTestName}: leaked ${name} property descriptor`);
  }
});
