import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { ProjectDeploymentParameterType } from '../../../../models/project-overview.model';
import { TaskService } from '../../../../services/task.service';

type DefinitionState = 'empty' | 'loading' | 'valid' | 'invalid';

/** Preview result state onto the shared status tones (AGT-3403). */
export const DEFINITION_RESULT_STATUS: Readonly<Record<DefinitionState, { tone: StatusTone; label: string }>> = {
  empty: { tone: 'neutral', label: 'Preview not generated' },
  loading: { tone: 'running', label: 'Validating definition...' },
  valid: { tone: 'success', label: 'Definition valid' },
  invalid: { tone: 'danger', label: 'Definition needs attention' },
};

interface ParameterDraft {
  id: number;
  name: string;
  type: ProjectDeploymentParameterType;
  required: boolean;
  defaultValue: string | boolean;
  label: string;
  help: string;
  options: string;
}

interface PreviewParameter extends ParameterDraft {
  optionsList: string[];
}

interface ParameterFieldErrors {
  name: string[];
  label: string[];
  defaultValue: string[];
  options: string[];
}

@Component({
  selector: 'app-deployment-definition-editor',
  standalone: true,
  imports: [FormsModule, PendingButtonDirective, StatusIndicatorComponent],
  templateUrl: './deployment-definition-editor.html',
  styleUrl: './deployment-definition-editor.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DeploymentDefinitionEditorComponent {
  private readonly tasks = inject(TaskService);
  private nextParameterId = 1;
  private validationRequest = 0;

  readonly projectName = input.required<string>();
  readonly command = signal('');
  readonly commandPlaceholder = 'bash scripts/deploy.sh --branch {{branch}}';
  readonly parameters = signal<ParameterDraft[]>([]);
  readonly state = signal<DefinitionState>('empty');
  readonly resultStatus = computed(() => DEFINITION_RESULT_STATUS[this.state()]);
  readonly commandError = signal<string | null>(null);
  readonly parameterErrors = signal<Record<number, ParameterFieldErrors>>({});
  readonly generalErrors = signal<string[]>([]);
  readonly preview = signal<PreviewParameter[]>([]);
  readonly advancedDefinition = computed(() => this.buildDefinition());

  addParameter(): void {
    const index = this.parameters().length + 1;
    this.parameters.update(parameters => [...parameters, {
      id: this.nextParameterId++,
      name: index === 1 ? 'branch' : `value${index}`,
      type: index === 1 ? 'branch' : 'string',
      required: true,
      defaultValue: index === 1 ? 'develop' : '',
      label: index === 1 ? 'Branch to deploy' : `Deployment value ${index}`,
      help: index === 1 ? 'Choose the repository branch that should be deployed.' : '',
      options: '',
    }]);
    this.resetValidation();
  }

  removeParameter(id: number): void {
    this.parameters.update(parameters => parameters.filter(parameter => parameter.id !== id));
    this.resetValidation();
  }

  updateParameter(id: number, patch: Partial<ParameterDraft>): void {
    this.parameters.update(parameters => parameters.map(parameter =>
      parameter.id === id ? { ...parameter, ...patch } : parameter));
    this.resetValidation();
  }

  changeType(id: number, type: ProjectDeploymentParameterType): void {
    this.updateParameter(id, { type, defaultValue: type === 'boolean' ? false : '' });
  }

  updateCommand(value: string): void {
    this.command.set(value);
    this.resetValidation();
  }

  hasParameterErrors(id: number): boolean {
    const errors = this.parameterErrors()[id];
    return !!errors && Object.values(errors).some(messages => messages.length > 0);
  }

  parameterFieldErrors(id: number, field: keyof ParameterFieldErrors): string[] {
    return this.parameterErrors()[id]?.[field] ?? [];
  }

  previewForm(): void {
    if (this.state() === 'loading') return;
    const errors = this.validateDraft();
    this.commandError.set(errors.command);
    this.parameterErrors.set(errors.parameters);
    this.generalErrors.set([]);
    this.preview.set([]);
    if (errors.command || Object.keys(errors.parameters).length) {
      this.state.set('invalid');
      return;
    }

    const requestId = ++this.validationRequest;
    this.state.set('loading');
    this.tasks.compileProjectDeployment(this.projectName(), this.buildDefinition()).subscribe({
      next: result => {
        if (requestId !== this.validationRequest) return;
        if (!result.runnable || result.warnings.length) {
          this.applyCompilerWarnings(result.warnings);
          this.state.set('invalid');
          return;
        }
        const drafts = this.parameters();
        this.preview.set(result.parameters.map((parameter, index) => {
          const draft = drafts.find(item => item.name.toLowerCase() === parameter.name.toLowerCase());
          return draft ? { ...draft, optionsList: splitOptions(draft.options) } : {
            id: -(index + 1), name: parameter.name, type: parameter.type, required: parameter.required,
            defaultValue: typeof parameter.default === 'boolean' ? parameter.default : '',
            label: parameter.name === 'confirm' ? 'Confirm deployment' : humanize(parameter.name),
            help: parameter.name === 'confirm' ? 'Required before this deployment can be started.' : '',
            options: '', optionsList: parameter.options,
          };
        }));
        this.state.set('valid');
      },
      error: () => {
        if (requestId !== this.validationRequest) return;
        this.generalErrors.set(['The definition could not be validated. Check the project connection and try again.']);
        this.state.set('invalid');
      },
    });
  }

  private validateDraft(): { command: string | null; parameters: Record<number, ParameterFieldErrors> } {
    const parameterErrors: Record<number, ParameterFieldErrors> = {};
    const names = new Set<string>();
    const command = this.command().trim();
    for (const parameter of this.parameters()) {
      const errors = emptyParameterErrors();
      const name = parameter.name.trim().toLowerCase();
      if (!/^[A-Za-z][A-Za-z0-9_-]*$/.test(parameter.name.trim())) {
        errors.name.push('Use a name that starts with a letter and contains only letters, numbers, dashes, or underscores.');
      } else if (names.has(name)) {
        errors.name.push('Parameter names must be unique.');
      }
      names.add(name);
      if (name && !command.toLowerCase().includes(`{{${name}}}`)) {
        errors.name.push(`Add {{${parameter.name.trim()}}} to the command or remove this parameter.`);
      }
      if (!parameter.label.trim()) errors.label.push('Add the label operators will see.');
      if (parameter.type === 'enum') {
        const options = splitOptions(parameter.options);
        if (options.length === 0) errors.options.push('Add at least one comma-separated option.');
        if (parameter.defaultValue !== '' && !options.includes(String(parameter.defaultValue))) {
          errors.defaultValue.push('Choose a default value that appears in the options list.');
        }
      }
      if (Object.values(errors).some(messages => messages.length)) parameterErrors[parameter.id] = errors;
    }
    const undeclared = [...command.matchAll(/\{\{([A-Za-z][A-Za-z0-9_-]*)\}\}/g)]
      .map(match => match[1])
      .filter(name => !names.has(name.toLowerCase()));
    return {
      command: !command
        ? 'Enter the repository script to run.'
        : undeclared.length ? `Add a parameter for {{${undeclared[0]}}}.` : null,
      parameters: parameterErrors,
    };
  }

  private applyCompilerWarnings(warnings: string[]): void {
    const commandWarnings: string[] = [];
    const parameterErrors: Record<number, ParameterFieldErrors> = {};
    for (const warning of warnings) {
      const parameter = this.parameters().find(item => warning.includes(`{{${item.name}}}`));
      if (parameter) {
        const errors = parameterErrors[parameter.id] ?? emptyParameterErrors();
        errors.name.push(warning);
        parameterErrors[parameter.id] = errors;
      }
      else commandWarnings.push(warning);
    }
    this.commandError.set(commandWarnings.join(' ') || null);
    this.parameterErrors.set(parameterErrors);
    if (!warnings.length) this.generalErrors.set(['The definition is not ready to run.']);
  }

  private buildDefinition(): string {
    const lines = [`Deployment for ${this.projectName()}`, `Command: ${this.command().trim()}`];
    for (const parameter of this.parameters()) {
      lines.push(`Parameter: ${parameter.name.trim()} ${parameter.type}${parameter.required ? '' : ' optional'}`);
      lines.push(`# Label: ${parameter.label.trim()}`);
      if (parameter.help.trim()) lines.push(`# Help: ${parameter.help.trim()}`);
      if (parameter.type !== 'secret-ref' && parameter.defaultValue !== '') lines.push(`# Default: ${String(parameter.defaultValue)}`);
      if (parameter.type === 'enum' && splitOptions(parameter.options).length) lines.push(`# Options: ${splitOptions(parameter.options).join(', ')}`);
    }
    return lines.join('\n');
  }

  private resetValidation(): void {
    this.validationRequest++;
    this.state.set('empty');
    this.commandError.set(null);
    this.parameterErrors.set({});
    this.generalErrors.set([]);
    this.preview.set([]);
  }
}

function splitOptions(value: string): string[] {
  return value.split(',').map(option => option.trim()).filter(Boolean);
}

function emptyParameterErrors(): ParameterFieldErrors {
  return { name: [], label: [], defaultValue: [], options: [] };
}

function humanize(value: string): string {
  return value.replace(/[-_]/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, first => first.toUpperCase());
}
