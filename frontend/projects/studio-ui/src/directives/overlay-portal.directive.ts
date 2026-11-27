import { Directive, ElementRef, OnDestroy, OnInit, inject, input } from '@angular/core';
import { OverlayPortalLayer, OverlayPortalRef, OverlayPortalService } from '../services/overlay-portal.service';

/**
 * Template-level bridge to the central overlay layer.
 *
 * Use this on modal, popover, dropdown, and popup root elements that are
 * conditionally rendered by Angular templates. The directive keeps the owning
 * component's bindings intact while moving the actual DOM node to <body>, so
 * fixed-position overlays are not clipped by ancestor overflow or local
 * stacking contexts.
 */
@Directive({
  selector: '[appOverlayPortal]',
  standalone: true,
})
export class OverlayPortalDirective implements OnInit, OnDestroy {
  readonly appOverlayPortal = input<OverlayPortalLayer>('panel');

  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly overlayPortal = inject(OverlayPortalService);
  private portalRef: OverlayPortalRef | null = null;

  ngOnInit(): void {
    this.portalRef = this.overlayPortal.attachLayer(this.host.nativeElement, this.appOverlayPortal());
  }

  ngOnDestroy(): void {
    this.portalRef?.dispose();
    this.portalRef = null;
  }
}
