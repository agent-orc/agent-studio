import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { AddHostWizardComponent } from './add-host-wizard';

describe('AddHostWizardComponent', () => {
  it('documents host-owned provider login without collecting a credential', async () => {
    await TestBed.configureTestingModule({
      imports: [AddHostWizardComponent],
      providers: [
        provideZonelessChangeDetection(),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(AddHostWizardComponent);
    const component = fixture.componentInstance;
    component.step.set(3);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Claude host login planned');
    expect(text).toContain('Codex host login planned');
    expect(text).toContain('Do not copy .credentials.json, auth.json, or a session from the operator device');
    expect(fixture.nativeElement.querySelector('input[type="password"]')).toBeNull();
  });
});
