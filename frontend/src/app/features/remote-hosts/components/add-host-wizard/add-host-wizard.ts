import { ChangeDetectionStrategy, Component, OnDestroy, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatComponent } from 'coding-agent-chat/composer';
import type { ChatMessage, ChatSubmitEvent } from 'coding-agent-chat/core';
import { Subscription } from 'rxjs';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';

export interface ProvisionedHostDraft {
  name: string;
  address: string;
}

interface WizardStep {
  label: string;
  eyebrow: string;
  title: string;
  description: string;
}

const STEPS: readonly WizardStep[] = [
  { label: 'Connect', eyebrow: 'Step 1 of 5', title: 'Connect to the host', description: 'Name the host and verify its SSH target before provisioning starts.' },
  { label: 'Provision', eyebrow: 'Step 2 of 5', title: 'Provision agent-host', description: 'Install the supported runtime, agent CLIs, browser dependencies, and agent-host binary.' },
  { label: 'Push key', eyebrow: 'Step 3 of 5', title: 'Give this host write access', description: 'Create one deploy key per host and repository, register the public key as write-enabled, and configure the SSH push URL.' },
  { label: 'CLI auth', eyebrow: 'Step 4 of 5', title: 'Authenticate agent CLIs', description: 'Provision Claude authentication through the protected SSH channel and verify the runner probe.' },
  { label: 'Smoke', eyebrow: 'Step 5 of 5', title: 'Run the smoke check', description: 'Confirm connectivity, registration, daemon startup, push capability, and one clean task handoff.' },
];

@Component({
  selector: 'app-add-host-wizard',
  standalone: true,
  imports: [FormsModule, ChatComponent],
  templateUrl: './add-host-wizard.html',
  styleUrl: './add-host-wizard.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AddHostWizardComponent implements OnDestroy {
  readonly cancelled = output<void>();
  readonly completed = output<ProvisionedHostDraft>();
  readonly step = signal(0);
  readonly name = signal('agent-runner-02');
  readonly address = signal('ssh://runner@host.example.com');
  readonly connectionChecked = signal(false);
  readonly provisioned = signal(false);
  readonly deployKeyReady = signal(false);
  readonly claudeAuthed = signal(false);
  readonly codexAuthed = signal(false);
  readonly providerAuthEnvironmentVariable = signal<'CLAUDE_CODE_OAUTH_TOKEN' | 'ANTHROPIC_API_KEY'>('CLAUDE_CODE_OAUTH_TOKEN');
  readonly providerAuthSecret = signal('');
  readonly providerAuthPhase = signal<'idle' | 'provisioning' | 'waiting' | 'ok' | 'unavailable' | 'error'>('idle');
  readonly providerAuthDetail = signal('No credential has been sent from this wizard.');
  readonly smokePassed = signal(false);
  readonly chatPending = signal(false);
  private readonly providerAuth = inject(ProviderAuthStatusService);
  private providerAuthSubscription: Subscription | null = null;
  readonly steps = STEPS;
  readonly current = computed(() => STEPS[this.step()]);
  readonly canContinue = computed(() => {
    switch (this.step()) {
      case 0: return !!this.name().trim() && !!this.address().trim() && this.connectionChecked();
      case 1: return this.provisioned();
      case 2: return this.deployKeyReady();
      case 3: return this.claudeAuthed() && this.codexAuthed();
      default: return this.smokePassed();
    }
  });
  readonly messages = signal<ChatMessage[]>([this.message(
    'orchestrator',
    'I will stay with you through setup. Start with an Ubuntu LTS host that accepts SSH key authentication. I can explain any check before you continue.',
  )]);

  next(): void {
    if (!this.canContinue()) return;
    if (this.step() === STEPS.length - 1) {
      this.completed.emit({ name: this.name(), address: this.address() });
      return;
    }
    this.step.update((value) => value + 1);
    const hints = [
      'Connection verified. Provision the host packages and publish agent-host to `/opt/agent-host`.',
      'Provisioning is ready. Generate the host deploy key, register it with write access, and configure the SSH push URL.',
      'Push identity is configured. Send the Claude credential once through the protected SSH channel, then verify the provider probe. Studio does not retain it.',
      'Both CLIs are authenticated. The final smoke check should prove `/healthz`, client registration, daemon startup, push capability, and task handoff.',
    ];
    this.messages.update((items) => [...items, this.message('orchestrator', hints[this.step() - 1])]);
  }

  back(): void { this.step.update((value) => Math.max(0, value - 1)); }

  ngOnDestroy(): void {
    this.providerAuthSubscription?.unsubscribe();
  }

  setProviderAuthEnvironmentVariable(value: string): void {
    if (value !== 'CLAUDE_CODE_OAUTH_TOKEN' && value !== 'ANTHROPIC_API_KEY') return;
    this.providerAuthEnvironmentVariable.set(value);
    this.providerAuthSecret.set('');
    this.claudeAuthed.set(false);
    this.providerAuthPhase.set('idle');
    this.providerAuthDetail.set('No credential has been sent from this wizard.');
  }

  provisionProviderAuth(): void {
    const secret = this.providerAuthSecret();
    const sshTarget = this.address().trim().replace(/^ssh:\/\//i, '');
    if (!sshTarget || secret.length < 16 || this.providerAuthPhase() === 'provisioning') return;
    const existingCredential = this.providerAuth.statuses().find(status =>
      status.provider === 'claude'
      && status.aliases.some(alias => alias.toLowerCase() === this.name().trim().toLowerCase()));
    const baseline = existingCredential?.advertisedAt ?? null;
    this.providerAuthSubscription?.unsubscribe();
    this.claudeAuthed.set(false);
    this.providerAuthPhase.set('provisioning');
    this.providerAuthDetail.set('Sending the credential through SSH stdin and installing the protected EnvironmentFile.');
    this.providerAuth.provision({
      sshTarget,
      runnerId: this.name().trim(),
      environmentVariable: this.providerAuthEnvironmentVariable(),
      secret,
      ...(existingCredential && this.providerAuthEnvironmentVariable() === 'ANTHROPIC_API_KEY'
        ? { idempotencyKey: `renewal-${crypto.randomUUID()}` } : {}),
    }).subscribe({
      next: response => {
        this.providerAuthSecret.set('');
        this.providerAuthPhase.set('waiting');
        this.providerAuthDetail.set(response.detail);
        if (response.operationId) {
          this.providerAuthSubscription = this.providerAuth.waitForRenewalCompletion(
            response.operationId, 15 * 60_000, sshTarget).subscribe({
            next: step => {
              this.providerAuthPhase.set(step === 'complete' ? 'ok' : 'unavailable');
              this.providerAuthDetail.set(step === 'complete'
                ? 'Both runner units verified the new generation with real provider requests.'
                : 'Credential recovery is required before this host can use the new generation.');
              this.claudeAuthed.set(step === 'complete');
            },
            error: () => {
              this.providerAuthPhase.set('waiting');
              this.providerAuthDetail.set('The protected credential is installed. Waiting for real requests from both runner units.');
            },
          });
          return;
        }
        if (!response.processEnvironmentVerified) return;
        this.providerAuthSubscription = this.providerAuth.waitForFreshProbe(
          'claude',
          [this.name().trim()],
          baseline,
        ).subscribe({
          next: status => {
            this.providerAuthPhase.set(status.state === 'ok' ? 'ok' : 'unavailable');
            this.providerAuthDetail.set(status.detail);
            this.claudeAuthed.set(status.state === 'ok');
          },
          error: () => {
            this.providerAuthPhase.set('waiting');
            this.providerAuthDetail.set('The daemon environment is verified, but no newer provider probe has arrived yet.');
          },
        });
      },
      error: error => {
        this.providerAuthSecret.set('');
        this.providerAuthPhase.set('error');
        this.providerAuthDetail.set(error?.error?.message ?? 'Provider authentication could not be provisioned. Studio retained no credential.');
      },
    });
  }

  onSubmit(event: ChatSubmitEvent): void {
    const text = event.text.trim();
    if (!text) return;
    this.messages.update((items) => [...items, this.message('user', text)]);
    this.chatPending.set(true);
    const reply = this.stepReply();
    setTimeout(() => {
      this.messages.update((items) => [...items, this.message('orchestrator', reply)]);
      this.chatPending.set(false);
    }, 250);
  }

  private stepReply(): string {
    switch (this.step()) {
      case 0: return 'Use an SSH URI for the runner account. The connection check should confirm key authentication, Ubuntu LTS, and sudo access.';
      case 1: return 'Install git, curl, build-essential, .NET 10, Node 22, Claude, Codex, and Playwright Chromium. Then publish `runner/AgentRunner.csproj` to `/opt/agent-host` and link `/opt/agent-runner` to it for migration compatibility.';
      case 2: return 'Run `ssh-keygen -t ed25519 -f ~/.ssh/agent-studio-deploy -N ""`, register the `.pub` key as a write-enabled repository deploy key, then set `RUNNER_GIT_PUSH_REMOTE` to the SSH repository URL.';
      case 3: return 'Enter a Claude OAuth token or Anthropic API key here. Studio sends it through SSH stdin, installs `/etc/agent-runner/provider-auth.env` as `root:agent` mode `640`, restarts the units, and waits for a fresh process-environment probe. The value never enters a task or the Studio database.';
      default: return 'Run `agent-host --health-check`, start `agent-host.service`, and send one ready task through this execution host. A clean external completion confirms the handoff.';
    }
  }

  private message(role: 'user' | 'orchestrator', text: string): ChatMessage {
    return { id: `${role}-${Date.now()}-${Math.random()}`, role, text, timestamp: new Date().toISOString() };
  }
}
