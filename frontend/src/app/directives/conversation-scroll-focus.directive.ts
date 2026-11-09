import { AfterViewInit, Directive, ElementRef, inject } from '@angular/core';

/** Gives the library transcript a keyboard stop in every Activity state. */
@Directive({ selector: 'cac-conversation-view[appConversationScrollFocus]', standalone: true })
export class ConversationScrollFocusDirective implements AfterViewInit {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  ngAfterViewInit(): void {
    const surface = this.host.nativeElement.querySelector<HTMLElement>('[data-testid="conversation-view"]');
    if (!surface) return;
    surface.tabIndex = 0;
    surface.setAttribute('aria-label', 'Conversation');
  }
}
