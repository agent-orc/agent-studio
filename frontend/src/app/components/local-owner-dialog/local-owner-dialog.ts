import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { DialogComponent } from '../dialog/dialog.component';
import { LocalOwnerSignInComponent } from '../local-owner-sign-in/local-owner-sign-in';
import { LocalOwnerSessionService } from '../../services/local-owner-session.service';

/** Asks for the owner account while an action waits in `LocalOwnerSessionService`, then lets the action finish. */
@Component({
  selector: 'app-local-owner-dialog',
  standalone: true,
  imports: [DialogComponent, LocalOwnerSignInComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './local-owner-dialog.html',
  styleUrl: './local-owner-dialog.scss',
})
export class LocalOwnerDialogComponent {
  readonly session = inject(LocalOwnerSessionService);
}
