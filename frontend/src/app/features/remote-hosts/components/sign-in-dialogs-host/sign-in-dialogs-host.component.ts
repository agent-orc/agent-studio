import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ViewContainerRef,
  inject,
  viewChild,
} from '@angular/core';

/** Starts loading provider sign-in dialogs after the board has rendered. */
@Component({
  selector: 'app-sign-in-dialogs-host',
  standalone: true,
  templateUrl: './sign-in-dialogs-host.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SignInDialogsHostComponent implements AfterViewInit {
  private readonly outlet = viewChild.required('outlet', { read: ViewContainerRef });
  private readonly destroyRef = inject(DestroyRef);

  ngAfterViewInit(): void {
    void this.load();
  }

  private async load(): Promise<void> {
    const { CodexSignInDialogComponent, ClaudeSignInDialogComponent } =
      await import('../../remote-sign-in-dialogs.lazy');
    if (this.destroyRef.destroyed) return;
    this.outlet().createComponent(CodexSignInDialogComponent);
    this.outlet().createComponent(ClaudeSignInDialogComponent);
  }
}
