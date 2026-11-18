import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { ClientService } from '../services/client.service';
import { PrincipalNamePipe } from './principal-name.pipe';

describe('PrincipalNamePipe', () => {
  function pipe(names: Record<string, string>) {
    const clients = {
      loadHumanNames: vi.fn(),
      resolve: (id: string) => ({ id, displayName: names[id] ?? id }),
    };
    TestBed.configureTestingModule({ providers: [{ provide: ClientService, useValue: clients }] });
    return { clients, pipe: TestBed.runInInjectionContext(() => new PrincipalNamePipe()) };
  }

  it('shows the display name of a user id and loads the directory for it', () => {
    const { clients, pipe: principal } = pipe({ usr_ecaff70110: 'Ada Owner' });

    expect(principal.transform('usr_ecaff70110')).toBe('Ada Owner');
    expect(clients.loadHumanNames).toHaveBeenCalled();
  });

  it('keeps an unknown id and passes an empty value through', () => {
    const { clients, pipe: principal } = pipe({});

    expect(principal.transform('operator')).toBe('operator');
    expect(principal.transform(null)).toBeNull();
    expect(clients.loadHumanNames).not.toHaveBeenCalled();
  });
});
