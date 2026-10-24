

namespace AgentStudio.Registry;

internal sealed class ProjectUrlStopException : Exception { }

/// <summary>
/// F45a — read-only surface for the workspace + project registries.
/// Lists workspaces with their embedded projects, returns project
/// details by PROJ-NNN id, and lists projects flat.
///
/// <para>Write endpoints (POST / PUT / DELETE) ship in F45b alongside
/// folder-skeleton creation and rename semantics. The new endpoints are
/// additive: existing <c>/api/projects/{projectName}/...</c> routes
/// continue to operate against display names, and the new
/// <c>/api/projects/{projId}</c> route is constrained to the canonical
/// <c>PROJ-NNN</c> shape so it cannot accidentally swallow a legacy name
/// like <c>settings</c> or a watched-project display name.</para>
/// </summary>
public static class RegistryEndpoints
{
    /// <summary>
    /// F66 — pure projection used by <c>GET /api/workspaces</c>. Extracted so
    /// the LEFT-JOIN invariant ("every workspace appears, even ones with no
    /// projects") can be locked in by a unit test without hosting Kestrel.
    /// Iteration anchors on <c>workspaces.List()</c> and the per-workspace
    /// projects list defaults to an empty list when no projects are mapped.
    /// </summary>
    public static List<WorkspaceListItem> BuildWorkspaceListing(
        WorkspaceRegistry workspaces,
        ProjectRegistry projects,
        bool includeArchived,
        Func<ProjectRecord, bool>? projectAllowed = null)
    {
        var projectsByWs = projects.List()
            .Where(p => includeArchived || !p.Archived)
            .Where(p => projectAllowed?.Invoke(p) ?? true)
            .GroupBy(p => p.WorkspaceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        return workspaces.List()
            .Where(w => projectAllowed is null || projectsByWs.ContainsKey(w.Id))
            .Select(w => new WorkspaceListItem
        {
            Id = w.Id,
            DisplayName = w.DisplayName,
            SortOrder = w.SortOrder,
            IsDefault = w.IsDefault,
            Color = w.Color,
            CreatedAt = w.CreatedAt,
            Projects = projectsByWs.TryGetValue(w.Id, out var list)
                ? list.Select(ProjectSummary.From).ToList()
                : [],
        }).ToList();
    }

    public static void MapRegistryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/projects/path-import", (ProjectPathImportRequest request, HttpContext context,
            ProjectRegistry projects, IConfiguration configuration) =>
        {
            var actor = (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal)?.User;
            if ((SecurityProfiles.IsNetworked(configuration) && actor?.Role != StudioRoles.Owner)
                || actor is not null && actor.Role != StudioRoles.Owner)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request.Mappings is null || request.Mappings.Count == 0
                || request.Mappings.Any(mapping => string.IsNullOrWhiteSpace(mapping.WindowsPrefix)
                    || string.IsNullOrWhiteSpace(mapping.ServerPrefix)))
                return Results.BadRequest(new { error = "At least one Windows-to-server path mapping is required." });
            var rows = projects.List().Select(project => ProjectPathImport.Plan(project, request.Mappings)).ToList();
            if (request.Apply) projects.ApplyPathImport(rows);
            return Results.Ok(new { applied = request.Apply, projects = rows });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        app.MapGet("/api/workspaces", (HttpContext context, WorkspaceRegistry workspaces, ProjectRegistry projects, bool? includeArchived) =>
        {
            var human = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal;
            Func<ProjectRecord, bool>? projectAllowed = human is null
                || ProjectAccessAuthorization.HasUnrestrictedProjectAccess(human.User)
                    ? null
                    : project => ProjectAccessAuthorization.Allows(human.User, project.Id, projects);
            return Results.Ok(BuildWorkspaceListing(
                workspaces,
                projects,
                includeArchived == true,
                projectAllowed));
        });

        app.MapGet("/api/projects", (HttpContext context, ProjectRegistry projects, bool? includeArchived) =>
        {
            var all = projects.List();
            if (includeArchived != true) all = [.. all.Where(p => !p.Archived)];
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human)
                all = [.. all.Where(project => ProjectAccessAuthorization.Allows(human.User, project.Id, projects))];
            return Results.Ok(all.Select(ProjectSummary.From).ToList());
        });

        // The {projId} route is constrained to PROJ-NNN so it cannot
        // collide with the legacy /api/projects/{projectName}/...
        // family (settings, Runbook, etc.) which uses display names.
        app.MapGet(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}", (string projId, ProjectRegistry projects) =>
        {
            var record = projects.FindById(projId);
            return record == null
                ? Results.NotFound(new { error = $"Unknown projectId '{projId}'" })
                : Results.Ok(record);
        });

        app.MapGet(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/{urlId}/preview/{**path}",
            ProjectUrlPreviewProxy.HandleAsync)
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        app.MapPost("/api/component-routing/resolve", (ComponentRoutingRequest body, ComponentRoutingService routing) =>
            Results.Ok(routing.Resolve(body ?? new ComponentRoutingRequest())));

        app.MapPut(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/ownership-mappings/{mappingId}",
            (string projId, string mappingId, ComponentOwnershipMapping body, HttpContext ctx, ProjectRegistry projects) =>
            {
                if (body == null) return Results.BadRequest(new { error = "body required" });
                try
                {
                    var actor = ctx.Request.Headers["X-Client-Id"].FirstOrDefault();
                    var updated = projects.UpsertOwnershipMapping(projId, body with { Id = mappingId }, actor);
                    return Results.Ok(updated.OwnershipMappings.First(row =>
                        string.Equals(row.Id, mappingId, StringComparison.OrdinalIgnoreCase)));
                }
                catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
                catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            });

        app.MapGet(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/ownership-mappings/audit",
            (string projId, ProjectRegistry projects) =>
            {
                var project = projects.FindById(projId);
                return project == null
                    ? Results.NotFound(new { error = $"Unknown projectId '{projId}'" })
                    : Results.Ok(project.OwnershipMappingAudit.OrderByDescending(row => row.ChangedAt));
            });

        // ----- F45b workspace mutations (ADR-0042) -----

        app.MapPost("/api/workspaces", (RegistryCreateWorkspaceRequest body, WorkspaceRegistry workspaces) =>
        {
            if (body == null || string.IsNullOrWhiteSpace(body.DisplayName))
                return Results.BadRequest(new { error = "displayName is required" });
            try
            {
                var created = workspaces.Create(body.DisplayName, body.Color);
                return Results.Created($"/api/workspaces/{created.Id}", created);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPut("/api/workspaces/{id}", (string id, UpdateWorkspaceRequest body, WorkspaceRegistry workspaces) =>
        {
            if (body == null) return Results.BadRequest(new { error = "body required" });
            try
            {
                WorkspaceRecord? result = null;
                if (body.DisplayName != null) result = workspaces.Rename(id, body.DisplayName);
                if (body.Color != null || body.ClearColor == true)
                    result = workspaces.SetColor(id, body.ClearColor == true ? null : body.Color);
                if (result == null) result = workspaces.Find(id);
                return result == null
                    ? Results.NotFound(new { error = $"Unknown workspaceId '{id}'" })
                    : Results.Ok(result);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/workspaces/{id}/reorder", (string id, WorkspaceReorderRequest body, WorkspaceRegistry workspaces) =>
        {
            if (body == null || (body.Direction != -1 && body.Direction != 1))
                return Results.BadRequest(new { error = "direction must be -1 or +1" });
            try
            {
                var list = workspaces.Reorder(id, body.Direction);
                return Results.Ok(list);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        app.MapDelete("/api/workspaces/{id}", (string id, WorkspaceRegistry workspaces, ProjectRegistry projects) =>
        {
            try
            {
                var result = workspaces.Delete(id, projects);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // ----- F45b project mutations (ADR-0042) -----

        app.MapPost("/api/projects", async (RegistryCreateProjectRequest body, ProjectRegistry projects, WorkspaceRegistry workspaces,
            WorkspaceManagementService workspaceManagement, TaskScannerService scanner, TaskWatcherService watcher,
            AgentStudio.Runner.TaskRunnerService runners, AgentStudio.Projects.ProjectSettingsService projectSettings,
            ClientIdentityStore clients, WikiContentCache wikiContentCache,
            AgentStudio.ExecutionPreparation.ProjectDefinitionProposalService definitionProposals,
            IProjectRepositoryProvisioner repositoryProvisioner,
            AgentStudio.Runner.BuildProfileValidationService profileValidation,
            TaskMutationService taskMutations, ILoggerFactory loggerFactory, IConfiguration configuration,
            HttpContext context, CancellationToken cancellationToken) =>
        {
            var onboardingStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (body == null)
                return Results.BadRequest(new { error = "body required" });
            var creatingActor = (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal)?.User;
            if (SecurityProfiles.IsNetworked(configuration) && creatingActor?.Role != StudioRoles.Owner
                && (!string.IsNullOrWhiteSpace(body.RepositoryUrl) || body.CreateRepository is not null
                    || !string.IsNullOrWhiteSpace(body.RepositoryPath)
                    || !string.IsNullOrWhiteSpace(body.RootPath)))
                return Results.Json(new { error = "owner-required" }, statusCode: StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(body.WorkspaceId))
                return Results.BadRequest(new { error = "workspaceId is required" });
            if (workspaces.Find(body.WorkspaceId) == null)
                return Results.NotFound(new { error = $"Unknown workspaceId '{body.WorkspaceId}'" });
            var sourceType = string.IsNullOrWhiteSpace(body.SourceType) ? ProjectSourceTypes.LocalFolder : body.SourceType.Trim();
            if (!string.Equals(sourceType, ProjectSourceTypes.LocalFolder, StringComparison.Ordinal))
                return Results.BadRequest(new { error = $"sourceType '{sourceType}' is not supported" });

            string displayName;
            try { displayName = ProjectRegistry.ValidateDisplayName(body.DisplayName); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            var allProjects = projects.List();
            if (allProjects.Any(project => string.Equals(
                    project.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)))
                return Results.Conflict(new { error = $"displayName '{displayName}' is already used." });
            var existingCodes = allProjects.Select(p => p.ShortCode);
            var shortCode = string.IsNullOrWhiteSpace(body.ShortCode)
                ? ShortCodeGenerator.Derive(displayName, existingCodes)
                : body.ShortCode.Trim().ToUpperInvariant();
            if (!ShortCodeGenerator.ValidateFormat(shortCode))
                return Results.BadRequest(new { error = "shortCode must be 2-6 chars, start with A-Z, and use A-Z or 0-9" });
            if (allProjects.Any(p => string.Equals(p.ShortCode, shortCode, StringComparison.OrdinalIgnoreCase)))
                return Results.Conflict(new { error = $"shortCode '{shortCode}' is already used." });

            if (body.CreateRepository is { } createRepository)
            {
                var repositoryError = ProjectRepositoryTemplates.Validate(createRepository);
                if (repositoryError is not null) return Results.BadRequest(new { error = repositoryError });
                if (!string.IsNullOrWhiteSpace(body.RepositoryPath) || !string.IsNullOrWhiteSpace(body.RepositoryUrl)
                    || !string.IsNullOrWhiteSpace(body.RootPath))
                    return Results.BadRequest(new { error = "Do not supply an existing repository path or URL when creating a repository." });
            }

            string? executionRunner;
            try { executionRunner = ExecutionRunnerAssignment.NormalizeAndValidate(body.ExecutionRunner, clients); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

            string? repositoryUrl;
            try { repositoryUrl = ProjectRegistry.ValidateRepositoryUrl(body.RepositoryUrl); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

            string? repositoryPath;
            string? rootPath;
            try
            {
                repositoryPath = ProjectRegistry.ValidateRepositoryPath(body.RepositoryPath);
                rootPath = ProjectRegistry.ValidateRootPath(
                    string.IsNullOrWhiteSpace(body.RootPath) ? repositoryPath : body.RootPath);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            string id;
            try { id = projects.AllocateNextId(); }
            catch (ProjectPersistenceException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
            var storage = workspaceManagement.CreateProjectStorage(displayName, id);
            if (storage.Outcome == WorkspaceManagementOutcome.BadRequest)
                return Results.BadRequest(new { error = storage.Error });
            if (storage.Outcome == WorkspaceManagementOutcome.Conflict)
                return Results.Conflict(new { error = storage.Error });

            ProvisionedRepository? provisioned = null;
            if (body.CreateRepository is { } newRepository)
            {
                try
                {
                    provisioned = await repositoryProvisioner.CreateAsync(newRepository, cancellationToken);
                    repositoryUrl = provisioned.Url;
                    repositoryPath = provisioned.CheckoutPath;
                    rootPath = provisioned.CheckoutPath;
                }
                catch (RepositoryProvisioningException ex)
                {
                    workspaceManagement.DeleteProjectStorage(storage.Entry!.Path);
                    return ex.Code == "name-taken"
                        ? Results.Conflict(new { error = ex.Message, code = ex.Code })
                        : Results.BadRequest(new { error = ex.Message, code = ex.Code });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    workspaceManagement.DeleteProjectStorage(storage.Entry!.Path);
                    loggerFactory.CreateLogger("ProjectCreate").LogError(ex,
                        "Repository provisioning failed for {Owner}/{Name}", newRepository.Owner, newRepository.Name);
                    return Results.Problem("Repository setup failed. Check the Studio host and retry.", statusCode: 500);
                }
            }

            var record = new ProjectRecord
            {
                Id = id,
                SourceType = sourceType,
                DisplayName = displayName,
                ShortCode = shortCode,
                WorkspaceId = body.WorkspaceId.Trim(),
                Color = string.IsNullOrWhiteSpace(body.Color) ? null : body.Color.Trim(),
                CliDefault = string.IsNullOrWhiteSpace(body.CliDefault) ? null : body.CliDefault.Trim(),
                ModelDefault = string.IsNullOrWhiteSpace(body.ModelDefault) ? null : body.ModelDefault.Trim(),
                SortOrder = allProjects.Count,
                NextTaskKeySeq = 1,
                NextWorkbenchKeySeq = 1,
                StorageLocation = storage.Entry?.Path ?? "",
                RepositoryPath = repositoryPath,
                RootPath = rootPath,
                Urls = repositoryUrl == null
                    ? []
                    : [new ProjectUrlRecord { Id = "repo", Label = "Repository", Url = repositoryUrl, SortOrder = 0 }],
                Archived = false,
                CreatedAt = DateTime.UtcNow,
            };

            ProjectRecord created;
            var appended = false;
            try
            {
                created = projects.Append(record, creatingActor);
                appended = true;
                if (executionRunner != null)
                {
                    projectSettings.RekeyProject(
                        created.DisplayName,
                        created.DisplayName,
                        updateExecutionRunner: true,
                        executionRunner: executionRunner,
                        remoteExecutionEnabled: true);
                }
            }
            catch (InvalidOperationException ex)
            {
                CleanupFailedCreate(record, appended, projects, workspaceManagement, loggerFactory);
                if (provisioned is not null)
                    await DeleteProvisionedRepositoryAsync(repositoryProvisioner, body.CreateRepository!, provisioned, loggerFactory);
                return Results.Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                CleanupFailedCreate(record, appended, projects, workspaceManagement, loggerFactory);
                if (provisioned is not null)
                    await DeleteProvisionedRepositoryAsync(repositoryProvisioner, body.CreateRepository!, provisioned, loggerFactory);
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException)
            {
                CleanupFailedCreate(record, appended, projects, workspaceManagement, loggerFactory);
                return Results.Json(new { error = "owner-required" }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ProjectPersistenceException ex)
            {
                // Append restores its in-memory snapshot when its own write
                // fails. A later settings failure leaves the record live and
                // must therefore be compensated before deleting its folder.
                var liveRecord = projects.FindById(record.Id);
                CleanupFailedCreate(
                    record,
                    appended && ReferenceEquals(liveRecord, record),
                    projects,
                    workspaceManagement,
                    loggerFactory);
                if (provisioned is not null)
                    await DeleteProvisionedRepositoryAsync(repositoryProvisioner, body.CreateRepository!, provisioned, loggerFactory);
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }

            if (provisioned is not null)
            {
                try
                {
                    projectSettings.SetBuildProfile(created.DisplayName, new BuildProfile
                    {
                        Stack = "node", InstallCmd = "npm ci", BuildCmds = ["npm run build"],
                        TestCmds = ["npm test"], Lockfiles = ["package-lock.json"],
                    });
                    var validation = await profileValidation.ValidateAsync(
                        created.DisplayName, provisioned.CheckoutPath, cancellationToken);
                    if (!validation.Green)
                        throw new InvalidOperationException($"Template build failed: {validation.Summary}");
                }
                catch (Exception ex)
                {
                    projectSettings.SetBuildProfile(created.DisplayName, null);
                    CleanupFailedCreate(record, true, projects, workspaceManagement, loggerFactory);
                    await DeleteProvisionedRepositoryAsync(repositoryProvisioner, body.CreateRepository!, provisioned, loggerFactory);
                    if (ex is OperationCanceledException) throw;
                    return Results.Problem($"Repository setup did not finish: {ex.Message}", statusCode: 500);
                }
            }

            var liveEntry = scanner.GetWatchPaths().First(entry =>
                string.Equals(entry.Path, created.StorageLocation, StringComparison.OrdinalIgnoreCase));
            watcher.EnsureWatching(liveEntry);
            wikiContentCache.Preload(liveEntry.Name);
            runners.EnsureRunner(liveEntry);
            var proposalTaskId = definitionProposals.CreateCard(created.DisplayName);
            if (provisioned is not null)
            {
                var firstCard = taskMutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
                {
                    Project = created.DisplayName,
                    WatchPath = liveEntry.Path,
                    Title = "Hello world change",
                    PromptMarkdown = "Make a small visible hello world change in the generated app. Update its test and run npm test and npm run build.",
                    TargetState = TaskStates.Preparation,
                    Mode = TaskModes.Coding,
                    TaskType = TaskTypes.Feature,
                    CreationSource = TimelineActors.Orchestrator,
                    CreatedBy = "Agent Studio",
                });
                if (firstCard is null)
                    loggerFactory.CreateLogger("ProjectCreate").LogWarning(
                        "First card could not be offered for project {Project}", created.Id);
            }
            var onboardingDurationMs = (long)System.Diagnostics.Stopwatch
                .GetElapsedTime(onboardingStarted).TotalMilliseconds;
            loggerFactory.CreateLogger("ProjectCreate").LogInformation(
                "project-onboarded id={Id} workspaceId={WorkspaceId} storage={Storage} repository={Repository} runner={Runner} preparationProposal={PreparationProposal} durationMs={DurationMs} questions={Questions} manualSteps={ManualSteps}",
                created.Id, created.WorkspaceId, created.StorageLocation,
                created.RepositoryPath ?? repositoryUrl ?? "(none)", executionRunner ?? "local",
                proposalTaskId ?? "unavailable", onboardingDurationMs, 0, 0);
            return Results.Created($"/api/projects/{created.Id}", ProjectSummary.From(created));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        app.MapPut(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}", (string projId, UpdateProjectRequest body,
            ProjectRegistry projects, WorkspaceRegistry workspaces,
            AgentStudio.Projects.ProjectSettingsService projectSettings,
            ClientIdentityStore clients, WikiContentCache wikiContentCache, ILoggerFactory loggerFactory,
            IConfiguration configuration, HttpContext context) =>
        {
            if (body == null) return Results.BadRequest(new { error = "body required" });
            var previous = projects.FindById(projId);
            if (previous == null)
                return Results.NotFound(new { error = $"Unknown projectId '{projId}'" });

            var currentRepositoryUrl = previous.Urls.FirstOrDefault(url =>
                string.Equals(url.Id, "repo", StringComparison.OrdinalIgnoreCase))?.Url;
            var repositoryUrlChanged = body.ClearRepositoryUrl == true
                ? currentRepositoryUrl is not null
                : body.RepositoryUrl != null && !string.Equals(
                    body.RepositoryUrl.Trim(), currentRepositoryUrl, StringComparison.Ordinal);
            var repositoryPathChanged = body.ClearRepositoryPath == true
                ? previous.RepositoryPath is not null
                : body.RepositoryPath != null && !string.Equals(
                    body.RepositoryPath.Trim(), previous.RepositoryPath, StringComparison.Ordinal);
            var rootPathChanged = body.ClearRootPath == true
                ? previous.RootPath is not null
                : body.RootPath != null && !string.Equals(
                    body.RootPath.Trim(), previous.RootPath, StringComparison.Ordinal);
            var human = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal;
            if ((repositoryUrlChanged || repositoryPathChanged || rootPathChanged) && SecurityProfiles.IsNetworked(configuration)
                && human?.User.Role != StudioRoles.Owner)
                return Results.Json(new { error = "owner-required" }, statusCode: StatusCodes.Status403Forbidden);

            var updateExecutionRunner = body.ExecutionRunner != null || body.ClearExecutionRunner == true;
            string? executionRunner = null;
            if (updateExecutionRunner)
            {
                try
                {
                    executionRunner = ExecutionRunnerAssignment.NormalizeAndValidate(
                        body.ClearExecutionRunner == true ? null : body.ExecutionRunner,
                        clients);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }

            try
            {
                var result = projects.Update(projId, body, workspaces, out previous, human?.User);
                if (body.RepositoryUrl != null || body.ClearRepositoryUrl == true
                    || body.RepositoryPath != null || body.ClearRepositoryPath == true)
                    clients.InvalidateRunnerProjectPreflights(projId);
                if (body.WikiSourceBranch != null || body.ClearWikiSourceBranch == true
                    || body.RepositoryPath != null || body.ClearRepositoryPath == true)
                    wikiContentCache.Invalidate(projId);
                if (!string.Equals(previous.DisplayName, result.DisplayName, StringComparison.Ordinal)
                    || updateExecutionRunner)
                {
                    try
                    {
                        projectSettings.RekeyProject(
                            previous.DisplayName,
                            result.DisplayName,
                            updateExecutionRunner,
                            executionRunner,
                            remoteExecutionEnabled: updateExecutionRunner ? true : null);
                    }
                    catch (ProjectPersistenceException ex)
                    {
                        try { projects.RollbackUpdate(result, previous); }
                        catch (Exception rollbackEx)
                        {
                            loggerFactory.CreateLogger("ProjectUpdate").LogCritical(
                                rollbackEx,
                                "project-update-compensation-failed id={Id} after settings error={Error}",
                                projId, ex.Message);
                        }
                        return Results.Problem(
                            "Could not persist project settings; the project update was rolled back where possible.",
                            statusCode: StatusCodes.Status500InternalServerError);
                    }
                }
                return Results.Ok(ProjectSummary.From(result));
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "owner-required" }, statusCode: StatusCodes.Status403Forbidden); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (ProjectPersistenceException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // F46 — destructive project delete. Removes the on-disk project
        // storage (every lane + task), drops the matching WatchPaths entry so
        // no ghost picker row survives, then removes the registry record.
        // Storage is deleted first so a failure aborts before any metadata is
        // touched — never leaving an orphan folder behind a dangling pointer.
        app.MapDelete(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}",
            (string projId, ProjectRegistry projects, ProjectUrlProcessService procs,
                WorkspaceManagementService workspaceManagement, ILoggerFactory loggerFactory) =>
        {
            var log = loggerFactory.CreateLogger("ProjectDelete");
            var record = projects.FindById(projId);
            if (record == null)
                return Results.NotFound(new { error = $"Unknown projectId '{projId}'" });

            // Stop repository-owned preview children before metadata and storage
            // disappear. The process service also repeats this at host shutdown.
            procs.StopProject(projId);
            var storageResult = workspaceManagement.DeleteProjectStorage(record.StorageLocation);
            if (storageResult.Outcome == WorkspaceManagementOutcome.BadRequest)
            {
                log.LogError(
                    "project-delete-storage-failed id={Id} storage={Storage} error={Error}",
                    record.Id, record.StorageLocation, storageResult.Error);
                return Results.Problem(
                    storageResult.Error ?? "Failed to delete project storage.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            try { projects.Delete(projId); }
            catch (KeyNotFoundException __ex) { SilentCatch.Note(__ex, "RegistryEndpoints: already removed; idempotent success"); /* already removed; idempotent success */ }

            log.LogInformation(
                "project-deleted id={Id} displayName={DisplayName} storage={Storage}",
                record.Id, record.DisplayName, record.StorageLocation);

            return Results.Ok(new
            {
                deletedId = record.Id,
                displayName = record.DisplayName,
                storageLocation = record.StorageLocation,
            });
        });

        // ----- Project URLs (per-project watchable dev-server / preview URLs) -----

        // Detection: scan the project's repository (package.json, angular.json,
        // README.md) and return suggestions the UI offers as one-click chips.
        // Never auto-applied; the user picks.
        app.MapGet(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/url-suggestions",
            (string projId, ProjectRegistry projects, ProjectUrlDetectionService detection) =>
        {
            var record = projects.FindById(projId);
            if (record == null)
                return Results.NotFound(new { error = $"Unknown projectId '{projId}'" });
            var suggestions = detection.Detect(record);
            return Results.Ok(suggestions);
        });

        app.MapPost(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls",
            (string projId, CreateProjectUrlRequest body, ProjectRegistry projects, ClientIdentityStore clients,
                HttpContext context) =>
        {
            if (body == null) return Results.BadRequest(new { error = "body required" });
            try
            {
                var actor = (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal)?.User;
                var updated = projects.AddUrl(projId, body.Label, body.Url, body.StartRule, actor);
                clients.InvalidateRunnerProjectPreflights(projId);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "owner-required" }, statusCode: StatusCodes.Status403Forbidden); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPut(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/{urlId}",
            (string projId, string urlId, UpdateProjectUrlRequest body, ProjectRegistry projects, ClientIdentityStore clients,
                HttpContext context) =>
        {
            if (body == null) return Results.BadRequest(new { error = "body required" });
            try
            {
                var actor = (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal)?.User;
                var updated = projects.UpdateUrl(projId, urlId, body.Label, body.Url, body.StartRule, actor);
                clients.InvalidateRunnerProjectPreflights(projId);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "owner-required" }, statusCode: StatusCodes.Status403Forbidden); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/{urlId}",
            (string projId, string urlId, ProjectRegistry projects, IProjectUrlProcessStopper procs, ClientIdentityStore clients,
                HttpContext context) =>
        {
            try
            {
                var actor = (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal)?.User;
                var updated = projects.RemoveUrl(projId, urlId, actor, () =>
                {
                    var stopped = procs.Stop(projId, urlId);
                    if (stopped is not null && stopped.State is not
                        (ProjectUrlProcessStates.Stopped or ProjectUrlProcessStates.Exited))
                        throw new ProjectUrlStopException();
                });
                clients.InvalidateRunnerProjectPreflights(projId);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "owner-required" }, statusCode: StatusCodes.Status403Forbidden); }
            catch (ProjectUrlStopException) { return Results.Problem("Could not stop the process for this URL. Stop it and retry before removing the URL."); }
        });

        app.MapPost(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/reorder",
            (string projId, ReorderProjectUrlsRequest body, ProjectRegistry projects, ClientIdentityStore clients,
                HttpContext context) =>
        {
            if (body == null) return Results.BadRequest(new { error = "body required" });
            try
            {
                var actor = (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal)?.User;
                var updated = projects.ReorderUrls(projId, body.OrderedUrlIds, actor);
                clients.InvalidateRunnerProjectPreflights(projId);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "owner-required" }, statusCode: StatusCodes.Status403Forbidden); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // AGT-2180: full actionable diagnosis (process, TCP, HTTP, content) in
        // the bounded, redacted diagnostic contract consumed by the Preview
        // offline card and the Settings quick setup.
        app.MapGet(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/{urlId}/diagnostic",
            async (string projId, string urlId, ProjectRegistry projects, ProjectUrlProcessService procs, CancellationToken ct) =>
        {
            var record = projects.FindById(projId);
            if (record == null) return Results.NotFound(new { error = $"Unknown projectId '{projId}'" });
            var url = record.Urls.FirstOrDefault(u => string.Equals(u.Id, urlId, StringComparison.Ordinal));
            if (url == null) return Results.NotFound(new { error = $"Unknown url id '{urlId}'" });
            return Results.Ok(await procs.ProbeAsync(record, url, ct));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        // Repository identity for the Preview context header. This reads Git
        // at the command's effective working directory, which may differ from
        // the project's top-level checkout.
        app.MapGet(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/{urlId}/context",
            (string projId, string urlId, ProjectRegistry projects, GitService git,
                AgentStudio.Projects.ProjectSettingsService settings) =>
        {
            var record = projects.FindById(projId);
            if (record == null) return Results.NotFound(new { error = $"Unknown projectId '{projId}'" });
            var url = record.Urls.FirstOrDefault(u => string.Equals(u.Id, urlId, StringComparison.Ordinal));
            if (url == null) return Results.NotFound(new { error = $"Unknown url id '{urlId}'" });

            string? cwd;
            if (url.StartRule != null)
            {
                try { cwd = ProjectUrlProcessService.ResolveWorkingDirectory(record, url.StartRule); }
                catch (InvalidOperationException) { cwd = url.StartRule.Cwd ?? record.RepositoryPath ?? record.RootPath; }
            }
            else
            {
                cwd = record.RepositoryPath ?? record.RootPath;
            }

            return Results.Ok(git.GetPreviewContext(
                record.DisplayName,
                cwd,
                settings.Get(record.DisplayName).IntegrationBranch));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        // Start/restart, inspect, and stop the owned dev-server lifecycle. The
        // bounded snapshot output powers the embed's in-place live console.
        app.MapPost(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/{urlId}/start",
            (string projId, string urlId, ProjectRegistry projects, IProjectUrlProcessStarter procs,
                IConfiguration configuration, HttpContext context) =>
        {
            if (SecurityProfiles.IsNetworked(configuration)
                && (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal)?.User.Role != StudioRoles.Owner)
                return Results.Json(new { error = "owner-required" }, statusCode: StatusCodes.Status403Forbidden);
            try
            {
                return projects.UseUrl<IResult>(projId, urlId, (record, url) =>
                {
                    if (url.StartRule == null || string.IsNullOrWhiteSpace(url.StartRule.Command))
                        return Results.BadRequest(new { error = "This URL has no start rule to run." });
                    try
                    {
                        return Results.Ok(procs.StartWithReadiness(record, url));
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                        var diagnostic = procs.Latest(record, url);
                        string? cwd;
                        try { cwd = ProjectUrlProcessService.ResolveWorkingDirectory(record, url.StartRule); }
                        catch (InvalidOperationException) { cwd = url.StartRule.Cwd ?? record.RepositoryPath ?? record.RootPath; }
                        return Results.BadRequest(new
                        {
                            error = ex.Message,
                            command = url.StartRule.Command,
                            cwd,
                            classification = diagnostic?.Classification,
                            occupyingProcessId = diagnostic?.OccupyingProcessId,
                            occupyingProcessName = diagnostic?.OccupyingProcessName,
                        });
                    }
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        app.MapGet(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/{urlId}/process",
            (string projId, string urlId, ProjectUrlProcessService procs) =>
        {
            var snapshot = procs.Get(projId, urlId);
            return snapshot == null ? Results.NoContent() : Results.Ok(snapshot);
        });

        app.MapDelete(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/{urlId}/process",
            (string projId, string urlId, ProjectUrlProcessService procs) =>
        {
            var snapshot = procs.Stop(projId, urlId);
            return snapshot == null
                ? Results.NotFound(new { error = "No process is owned for this URL." })
                : Results.Ok(snapshot);
        });

        // Browser no-cors probes hide HTTP status. Probe the registry-owned URL
        // on the host so previews never mistake an HTTP error page for healthy.
        app.MapGet(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/{urlId}/readiness",
            async (string projId, string urlId, HttpRequest request,
                ProjectRegistry projects, ProjectUrlReadinessService readiness,
                CancellationToken cancellationToken) =>
        {
            var record = projects.FindById(projId);
            if (record == null)
                return Results.NotFound(new { error = $"Unknown projectId '{projId}'" });
            var url = record.Urls.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, urlId, StringComparison.Ordinal));
            if (url == null)
                return Results.NotFound(new { error = $"Unknown url id '{urlId}'" });

            string? studioOrigin = null;
            if (Uri.TryCreate(request.Headers.Referer.FirstOrDefault(), UriKind.Absolute, out var referer))
                studioOrigin = referer.GetLeftPart(UriPartial.Authority);
            return Results.Ok(await readiness.ProbeAsync(
                record, url, studioOrigin, cancellationToken));
        });

        // AGT-2180: Settings quick setup — validate a candidate configuration
        // with a bounded start + readiness run whose process never outlives
        // the request. Returns the diagnostic contract, never a saved URL.
        app.MapPost(@"/api/projects/{projId:regex(^PROJ-\d{{3,}}$)}/urls/test",
            async (string projId, TestProjectUrlRequest body, ProjectRegistry projects, ProjectUrlProcessService procs,
                IConfiguration configuration, HttpContext context, CancellationToken ct) =>
        {
            if (body == null) return Results.BadRequest(new { error = "body required" });
            if (body.StartRule is not null && SecurityProfiles.IsNetworked(configuration)
                && (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal)?.User.Role != StudioRoles.Owner)
                return Results.Json(new { error = "owner-required" }, statusCode: StatusCodes.Status403Forbidden);
            var record = projects.FindById(projId);
            if (record == null) return Results.NotFound(new { error = $"Unknown projectId '{projId}'" });
            var candidate = new ProjectUrlRecord
            {
                Id = "setup-test", Label = body.Label ?? "URL Preview setup", Url = body.Url ?? "",
                StartRule = body.StartRule,
            };
            return Results.Ok(await procs.TestAsync(record, candidate, ct));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);
    }

    private static async Task DeleteProvisionedRepositoryAsync(
        IProjectRepositoryProvisioner provisioner, CreateRepositoryRequest request,
        ProvisionedRepository repository, ILoggerFactory loggerFactory)
    {
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await provisioner.DeleteAsync(request, repository.CheckoutPath, cleanupTimeout.Token); }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("ProjectCreate").LogCritical(ex,
                "project-create-repository-cleanup-failed owner={Owner} name={Name}", request.Owner, request.Name);
        }
    }

    private static void CleanupFailedCreate(
        ProjectRecord record,
        bool appended,
        ProjectRegistry projects,
        WorkspaceManagementService workspaceManagement,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ProjectCreate");
        if (appended)
        {
            try { projects.RollbackAppend(record); }
            catch (Exception ex)
            {
                logger.LogCritical(ex,
                    "project-create-registry-compensation-failed id={Id} storage={Storage}",
                    record.Id, record.StorageLocation);
            }
        }

        var cleanup = workspaceManagement.DeleteProjectStorage(record.StorageLocation);
        if (cleanup.Outcome != WorkspaceManagementOutcome.Ok)
        {
            logger.LogCritical(
                "project-create-storage-cleanup-failed id={Id} storage={Storage} error={Error}",
                record.Id, record.StorageLocation, cleanup.Error);
        }
    }
}

/// <summary>POST /api/projects/{id}/urls payload.</summary>
public sealed record CreateProjectUrlRequest
{
    public string Label { get; init; } = "";
    public string Url { get; init; } = "";
    public ProjectUrlStartRule? StartRule { get; init; }
}

/// <summary>PUT /api/projects/{id}/urls/{urlId} payload.</summary>
public sealed record UpdateProjectUrlRequest
{
    public string Label { get; init; } = "";
    public string Url { get; init; } = "";
    public ProjectUrlStartRule? StartRule { get; init; }
}

/// <summary>POST /api/projects/{id}/urls/reorder payload.</summary>
public sealed record ReorderProjectUrlsRequest
{
    public List<string> OrderedUrlIds { get; init; } = [];
}

/// <summary>F45b — POST /api/workspaces payload.</summary>
public sealed record RegistryCreateWorkspaceRequest
{
    public string DisplayName { get; init; } = "";
    public string? Color { get; init; }
}

/// <summary>
/// F45b — PUT /api/workspaces/{id} payload. Each field is optional; only
/// non-null fields are applied. Set <see cref="ClearColor"/> = true to clear
/// the color (cannot be expressed by sending null because null also means
/// "leave unchanged").
/// </summary>
public sealed record UpdateWorkspaceRequest
{
    public string? DisplayName { get; init; }
    public string? Color { get; init; }
    public bool? ClearColor { get; init; }
}

/// <summary>F45b — POST /api/workspaces/{id}/reorder payload.</summary>
public sealed record WorkspaceReorderRequest
{
    /// <summary>-1 = move up one slot; +1 = move down one slot.</summary>
    public int Direction { get; init; }
}

/// <summary>F46 — POST /api/projects payload.</summary>
public sealed record RegistryCreateProjectRequest
{
    public CreateRepositoryRequest? CreateRepository { get; init; }
    public string? SourceType { get; init; }
    public string WorkspaceId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? ShortCode { get; init; }
    public string? CliDefault { get; init; }
    public string? ModelDefault { get; init; }
    public string? Color { get; init; }
    /// <summary>Absolute local Git checkout path.</summary>
    public string? RepositoryPath { get; init; }
    /// <summary>Optional browser/clone URL, stored as the well-known <c>repo</c> URL.</summary>
    public string? RepositoryUrl { get; init; }
    /// <summary>Optional remote runner id assigned immediately after creation.</summary>
    public string? ExecutionRunner { get; init; }
    /// <summary>
    /// Optional CLI working directory, set at onboarding time so auto-pickup
    /// has a runner from the first boot instead of silently having none
    /// until someone notices the mode toggle failing (see
    /// <see cref="ProjectRecord.RootPath"/>).
    /// </summary>
    public string? RootPath { get; init; }
}

/// <summary>AGT-2180 — POST /api/projects/{id}/urls/test payload (quick setup validation).</summary>
public sealed record TestProjectUrlRequest
{
    public string? Label { get; init; }
    public string? Url { get; init; }
    public ProjectUrlStartRule? StartRule { get; init; }
}

public static class ProjectSourceTypes
{
    public const string LocalFolder = "local-folder";
}

/// <summary>
/// F45b — PUT /api/projects/{PROJ-NNN} payload. Same optional-field
/// semantics as <see cref="UpdateWorkspaceRequest"/>.
/// </summary>
public sealed record UpdateProjectRequest
{
    public string? DisplayName { get; init; }
    public string? ShortCode { get; init; }
    public string? Color { get; init; }
    public bool? ClearColor { get; init; }
    public string? WorkspaceId { get; init; }
    /// <summary>Absolute repo checkout path; see <see cref="ProjectRecord.RepositoryPath"/>.</summary>
    public string? RepositoryPath { get; init; }
    public bool? ClearRepositoryPath { get; init; }
    /// <summary>Optional branch/ref used as the read-only source of the complete wiki.</summary>
    public string? WikiSourceBranch { get; init; }
    public bool? ClearWikiSourceBranch { get; init; }
    /// <summary>Absolute CLI working directory; see <see cref="ProjectRecord.RootPath"/>.</summary>
    public string? RootPath { get; init; }
    public bool? ClearRootPath { get; init; }
    /// <summary>Browser/clone URL maintained as the well-known <c>repo</c> project URL.</summary>
    public string? RepositoryUrl { get; init; }
    public bool? ClearRepositoryUrl { get; init; }
    public string? CliDefault { get; init; }
    public bool? ClearCliDefault { get; init; }
    public string? ModelDefault { get; init; }
    public bool? ClearModelDefault { get; init; }
    /// <summary>
    /// Optional convenience delegation to the established project-settings
    /// runner assignment. This value is never stored on ProjectRecord.
    /// </summary>
    public string? ExecutionRunner { get; init; }
    public bool? ClearExecutionRunner { get; init; }
    public bool? Archived { get; init; }
}

/// <summary>
/// API DTO returned by <c>GET /api/workspaces</c>. Mirrors
/// <see cref="WorkspaceRecord"/> plus an inline list of projects so the
/// client can render the sidebar with a single round-trip.
/// </summary>
public sealed record WorkspaceListItem
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int SortOrder { get; init; }
    public bool IsDefault { get; init; }
    public string? Color { get; init; }
    public DateTime CreatedAt { get; init; }
    public List<ProjectSummary> Projects { get; init; } = [];
}

/// <summary>
/// Flat-list shape for <c>GET /api/projects</c> and the embedded list
/// inside <c>WorkspaceListItem.Projects</c>. Omits the
/// <see cref="ProjectRecord.NextTaskKeySeq"/> counter to avoid surfacing
/// internal state on a list call - <c>GET /api/projects/{id}</c> returns
/// the full record for callers that need it.
/// </summary>
public sealed record ProjectSummary
{
    public string SourceType { get; init; } = ProjectSourceTypes.LocalFolder;
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ShortCode { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string? Color { get; init; }
    public string? CliDefault { get; init; }
    public string? ModelDefault { get; init; }
    public int SortOrder { get; init; }
    public string StorageLocation { get; init; } = "";
    public string? RepositoryPath { get; init; }
    public string? RootPath { get; init; }
    /// <summary>Well-known repository URL projected from <see cref="Urls"/>.</summary>
    public string? RepositoryUrl { get; init; }
    public string? WikiSourceBranch { get; init; }
    /// <summary>Configured watchable URLs, ordered; empty for most projects.</summary>
    public IReadOnlyList<ProjectUrlRecord> Urls { get; init; } = [];
    public IReadOnlyList<ComponentOwnershipMapping> OwnershipMappings { get; init; } = [];
    public bool Archived { get; init; }
    public DateTime CreatedAt { get; init; }

    public static ProjectSummary From(ProjectRecord p) => new()
    {
        Id = p.Id,
        SourceType = p.SourceType,
        DisplayName = p.DisplayName,
        ShortCode = p.ShortCode,
        WorkspaceId = p.WorkspaceId,
        Color = p.Color,
        CliDefault = p.CliDefault,
        ModelDefault = p.ModelDefault,
        SortOrder = p.SortOrder,
        StorageLocation = p.StorageLocation,
        RepositoryPath = p.RepositoryPath,
        RootPath = p.RootPath,
        RepositoryUrl = p.Urls.FirstOrDefault(url =>
            string.Equals(url.Id, "repo", StringComparison.OrdinalIgnoreCase))?.Url,
        Urls = [.. p.Urls.OrderBy(u => u.SortOrder)],
        OwnershipMappings = p.OwnershipMappings,
        WikiSourceBranch = p.WikiSourceBranch,
        Archived = p.Archived,
        CreatedAt = p.CreatedAt,
    };
}
