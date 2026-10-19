import { pipelineFromDefinition, pipelineFromExecution, stepKindLabel } from './deploy-pipeline.model';
import { executionLifecycleFixture, invalidExecutionFixture, pipelineDefinitionFixture } from '../testing/deploy-fixtures';

describe('deploy pipeline model', () => {
  it('draws the AGT-3221 diagram as gate, artifact, then environments with static-site and container details', () => {
    const view = pipelineFromDefinition(pipelineDefinitionFixture());

    expect(view.valid).toBe(true);
    expect(view.nodes!.map(node => node.id)).toEqual(['gate', 'artifact', 'staging', 'production']);
    expect(view.nodes![0].items).toEqual(['npm run build', 'npm test']);
    expect(view.nodes![1].items).toEqual(['web (static, dist)', 'api (container, Dockerfile, registry.example/website-api)']);
    expect(view.nodes![2]).toMatchObject({ automatic: true, approval: [], requires: 'artifact' });
    expect(view.nodes![3]).toMatchObject({ automatic: false, approval: ['Owner', 'Maintainer'], requires: 'staging' });
    expect(view.nodes![2].steps).toEqual([
      { kind: 'static-site', artifact: 'web', target: 'web-vm', destination: '/srv/www/website', health: '/healthz', healthTimeoutSeconds: 60 },
      { kind: 'container', artifact: 'api', target: 'app-vm', destination: '/srv/api/compose.yml (api)', health: '/healthz', healthTimeoutSeconds: 60 },
    ]);
  });

  it('keeps the AGT-3221 diagram of an invalid file next to its located issues', () => {
    const read = { ...pipelineDefinitionFixture(), valid: false,
      issues: [{ path: 'deploy[1].service', code: 'deploy-service-required', message: 'container needs a service.', line: 31 }] };
    const view = pipelineFromDefinition(read);

    expect(view.valid).toBe(false);
    expect(view.issues).toEqual([{ path: 'deploy[1].service', message: 'container needs a service.', line: 31 }]);
    expect(view.nodes).toHaveLength(4);
  });

  it('separates "no deployment declared" from "nothing could be parsed"', () => {
    const undeclared = { ...pipelineDefinitionFixture(), diagram: { ...pipelineDefinitionFixture().diagram!, environments: [] } };
    expect(pipelineFromDefinition(undeclared).nodes).toEqual([]);
    expect(pipelineFromDefinition({ valid: false, issues: [], diagram: null }).nodes).toBeNull();
    expect(pipelineFromExecution({ valid: true, lifecycle: { declared: false, nodes: [] } }).nodes).toEqual([]);
    expect(pipelineFromExecution({ valid: false, lifecycle: null }).nodes).toBeNull();
  });

  it('reads this server\'s lifecycle projection, valid or not', () => {
    const valid = pipelineFromExecution(executionLifecycleFixture());
    expect(valid.nodes![2].steps[0]).toEqual({
      kind: 'publish-static', artifact: 'web', target: 'web-vm', destination: 'deploy/website', health: null, healthTimeoutSeconds: null,
    });

    const invalid = pipelineFromExecution(invalidExecutionFixture());
    expect(invalid.valid).toBe(false);
    expect(invalid.issues).toEqual([{ path: 'deploy[0].step', line: null,
      message: 'P1 deploy step must be publish-static, start-container, run, health, or approval.' }]);
    expect(invalid.nodes![2].steps[0].kind).toBe('static-site');
  });

  it('labels every project.yml step kind and keeps an unknown kind readable', () => {
    for (const kind of ['static-site', 'container', 'publish-static', 'start-container', 'run', 'health', 'approval']) {
      expect(stepKindLabel(kind)).not.toBe(kind);
    }
    expect(stepKindLabel('ftp-upload')).toBe('ftp-upload');
  });
});
