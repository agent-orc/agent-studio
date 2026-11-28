using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.GitHubApp;

internal static class GitHubCallerCatalogue
{
    // The caller classes from AGT-4432. Receipt slots distinguish installations, checkouts and workflow jobs.
    internal static readonly IReadOnlyList<GitHubCallerDefinition> All =
    [
        new("app-owner-flow", "App registration and installation adoption", "App browser authorization and App API", "GitHub owner authorization and App grant"),
        new("app-broker", "App coverage, token issue and revocation", "App broker: Studio or Task Server", "App private key, adopted installations and host service binding"),
        new("coding-source", "Coding clone and source fetch", "Coding daemon and shared checkout", "Role App token, first-run deploy key or host Git"),
        new("coding-publish", "Coding result publication, probes and salvage", "Coding daemon and platform Git", "Role App token or host Git on effective push target"),
        new("work-container-source", "Isolated coding source", "Work container, attempt bootstrap and Kubernetes", "Attempt App read grant or anonymous GitHub"),
        new("review-probe", "Review source read probe", "Review daemon", "First-run credential, role App read token or review fallback"),
        new("review-private-source", "Review source and exact-result fetch", "Review container and detached worker", "First-run credential, role App read token, review fallback or Studio bundle"),
        new("review-base", "Review integration-base refresh", "Review container and source cache", "Scoped GitHub read or Studio bundle"),
        new("review-local", "Review checkout and cache materialization", "Review cache objects and local checkout", "Upstream source acquisition", true),
        new("review-push", "Review push", "Review executor has no built-in push", "Studio result-reporting identity", true),
        new("gate-source", "Gate direct-source compatibility fetch", "Gate daemon review materializer", "Scoped GitHub read or cached objects"),
        new("gate-bundle", "Gate bundle checkout and pinned after-merge run", "Gate daemon and older SSH worker", "Studio bundle and local checkout", true),
        new("gate-push", "Gate push", "Gate executor has no built-in push", "Studio result-reporting identity", true),
        new("after-merge-observer", "After-merge observation", "Coding daemon", "Coding role App route or host Git"),
        new("after-merge-window", "Scheduled after-merge window", "Window timer account and checkout", "Named role App store or host Git"),
        new("after-merge-nightly", "Scheduled after-merge nightly", "Nightly timer account and checkout", "Named role App store or host Git"),
        new("coverage", "Scheduled coverage fetch", "Coverage timer account and checkout", "Named role App store or host Git"),
        new("post-work-built-in", "Built-in post-work execution", "Post role step executor", "Studio lease and work directory", true),
        new("post-work-command", "Configured post-work commands", "Post role project commands", "Project-defined dependencies"),
        new("integration", "Integration fetch and candidate push", "Integration executor", "Role App write token or host Git"),
        new("studio-checkout", "Studio checkout and Git maintenance", "Studio backend and project checkouts", "Studio App route, explicit first-run credentials or host Git"),
        new("website-control", "Website workflow dispatch and status", "Studio publishing and deployment reconciliation", "Effective GitHub CLI authorization"),
        new("website-workflow", "Website workflow job", "Target website repository and workflow job", "Actions job token or workflow-owned grant"),
        new("package-publish", "Package branch and tag publish", "Studio version-tag publication", "Studio App route or host Git"),
        new("package-tracking", "Package workflow tracking", "Studio follow-up run lookup", "Effective GitHub CLI authorization"),
        new("package-workflow", "Package workflow job", "Project repository workflow job", "Actions job token or workflow-owned grant"),
        new("release-command", "Declared project release command", "Studio isolated release worktree", "Project-defined or inherited GitHub credential"),
        new("provisioning-api", "Repository create, seed and rollback", "Studio onboarding API", "Provisioning grant"),
        new("provisioning-clone", "First clone of provisioned repository", "Studio onboarding checkout", "Explicit provisioning grant"),
        new("promotion", "Product promotion and release tag", "Operator promotion checkout", "Host Git, reported promotion deploy key"),
        new("product-actions", "Product release Actions job", "Product checkout, release creation, attestations and image publication", "GitHub Actions job token and permissions"),
        new("release-download", "Release binaries and image availability", "Installer and release verification job", "Anonymous public delivery or private delivery grant"),
        new("workspace-bootstrap", "Workspace bootstrap and migration", "Task Server operator checkout", "Host Git, candidate workspace deploy key"),
        new("workspace-export", "Workspace export push", "Task Server periodic export service", "Host Git on effective push target"),
        new("workspace-recovery", "Workspace recovery and restore", "Recovery checkout and restore job", "Host Git or separately supplied recovery artifact"),
        new("task-server-ref-gc", "Result-ref deletion", "Task Server result-ref garbage collection", "Host Git on result repository"),
        new("stable-watcher", "Stable watcher and checkout refresh", "Deployment scheduler and Stable checkout", "Host Git or anonymous GitHub read"),
        new("update-service", "Update Service probes and release fetch", "Checkout-backed Stable Update Service", "Host Git or anonymous GitHub read"),
        new("networked-rollout", "Networked Studio and website rollout", "Deployment controller and target", "GHCR delivery grant or anonymous pull, plus separate host identity"),
        new("review-image", "Review image refresh", "Review image build and target container host", "GHCR grant for private pulls and image push"),
        new("project-deploy-artifact", "Studio deploy artifact handoff", "Deploy host poll, lease and artifact", "Studio deploy-host identity", true),
        new("project-deploy-image", "Project image deploy or configured steps", "Deploy job and application target", "GHCR delivery grant or project-defined GitHub dependency"),
        new("legacy-maintenance", "Legacy credential inspection and deploy-key maintenance", "Operator renewal command", "Explicit inspection credential or workstation GitHub CLI authorization"),
        new("structure-library", "Structure library release metadata", "Studio connectivity service", "Anonymous GitHub read or configured bearer"),
        new("hosted-wiki", "Hosted Wiki publication fetch", "Studio publication checkout", "Studio App route or host Git when upstream fetch is needed"),
    ];
}
