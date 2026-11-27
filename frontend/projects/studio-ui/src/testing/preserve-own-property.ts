import { afterEach, beforeEach } from 'vitest';

/**
 * Saves `owner[key]`'s own property descriptor before each test of the
 * calling suite and puts it back afterwards. Spec files share globals within
 * a worker (`isolate: false`), so a stub that outlives its test can break an
 * unrelated spec. Call it before the hook or test that installs the stub.
 */
export function preserveOwnProperty(owner: object, key: PropertyKey): void {
  let descriptor: PropertyDescriptor | undefined;
  beforeEach(() => {
    descriptor = Object.getOwnPropertyDescriptor(owner, key);
  });
  afterEach(() => {
    if (descriptor) Object.defineProperty(owner, key, descriptor);
    else Reflect.deleteProperty(owner, key);
  });
}
