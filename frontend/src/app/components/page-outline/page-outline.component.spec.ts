import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { PageOutlineComponent, type OutlineItem } from './page-outline.component';

describe('PageOutlineComponent', () => {
  const items: OutlineItem[] = [
    { id: 'outline-people', label: 'People', count: 6, group: 'Who' },
    { id: 'outline-groups', label: 'Groups', count: 0 },
    { id: 'outline-audit', label: 'Factor activity', group: 'Audit' },
  ];

  function mount() {
    TestBed.configureTestingModule({ imports: [PageOutlineComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(PageOutlineComponent);
    fixture.componentRef.setInput('items', items);
    fixture.detectChanges();
    return fixture;
  }

  it('lists every section with its count under its group heading', () => {
    const root = mount().nativeElement as HTMLElement;

    expect(root.querySelector('nav')!.getAttribute('aria-label')).toBe('On this page');
    expect([...root.querySelectorAll('.outline__group')].map(node => node.textContent)).toEqual(['Who', 'Audit']);
    const people = root.querySelector('[data-testid="page-outline-outline-people"]')!;
    expect(people.textContent).toContain('People');
    expect(people.querySelector('.count-badge')!.textContent!.trim()).toBe('6');
    expect(root.querySelector('[data-testid="page-outline-outline-groups"] .count-badge')!.textContent!.trim()).toBe('0');
    expect(root.querySelector('[data-testid="page-outline-outline-audit"] .count-badge')).toBeNull();
  });

  it('jumps to a section, marks it as the current location and focuses its heading', () => {
    const section = document.createElement('section');
    section.id = 'outline-groups';
    const heading = document.createElement('h3');
    heading.id = 'outline-groups-title';
    heading.tabIndex = -1;
    section.append(heading);
    document.body.append(section);
    const scrolled: unknown[] = [];
    section.scrollIntoView = options => { scrolled.push(options); };
    const fixture = mount();
    const root = fixture.nativeElement as HTMLElement;

    (root.querySelector('[data-testid="page-outline-outline-groups"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(scrolled).toHaveLength(1);
    expect(root.querySelector('[data-testid="page-outline-outline-groups"]')!.getAttribute('aria-current')).toBe('location');
    expect(root.querySelector('[data-testid="page-outline-outline-people"]')!.getAttribute('aria-current')).toBeNull();
    expect(document.activeElement).toBe(heading);
    section.remove();
  });
});
