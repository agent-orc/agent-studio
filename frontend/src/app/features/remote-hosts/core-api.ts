/** Remote-host state and policy used by the initial board shell. */
export { RemoteHostsService } from './services/remote-hosts.service';
export { ReviewQueueService } from './services/review-queue.service';
export { ProviderAuthStatusService } from './services/provider-auth-status.service';
export { CodexSignInDialogService } from './services/codex-sign-in-dialog.service';
export { ClaudeSignInDialogService } from './services/claude-sign-in-dialog.service';
export { modelCliVersionWaitReason, providerAuthWaitReason } from './models/provider-auth.model';
export { deriveBoardRunningTruth } from './models/running-truth';
export { hostExecutorRole } from './models/remote-host.model';
export type { HostTelemetryPoint, RemoteHost } from './models/remote-host.model';
