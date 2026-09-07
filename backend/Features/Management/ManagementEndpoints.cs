namespace AgentStudio.Management;

public static class ManagementEndpoints
{
    public static void MapManagementEndpoints(this WebApplication app)
    {
        app.MapGet("/recovery", () => Results.Content(RecoveryConsole.Html, "text/html"));
        var group = app.MapGroup("/api/v1/management");
        group.MapGet("/status", (HttpContext context, ManagementService service, IConfiguration configuration) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryAuthorize(context, configuration, out var denied, out _, out _)) return denied!;
            return Results.Ok(service.Snapshot($"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}"));
        });
        group.MapGet("/diagnostics", (HttpContext context, ManagementService service, IConfiguration configuration) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryAuthorize(context, configuration, out var denied, out _, out _)) return denied!;
            return Results.Ok(service.Diagnostics());
        });
        group.MapGet("/remote-hosts", (
            HttpContext context,
            AgentStudio.Runner.V1ReviewExecutorRegistry registry,
            IConfiguration configuration) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryAuthorize(context, configuration, out var denied, out _, out _)) return denied!;
            return Results.Ok(registry.ListCapabilitySnapshots());
        });
        group.MapGet("/remote-hosts/link-health", async (
            HttpContext context,
            RemoteRunnerLinkService links,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryAuthorize(context, configuration, out var denied, out _, out _)) return denied!;
            return Results.Ok(await links.SnapshotAsync(ct));
        });
        group.MapPost("/remote-hosts/{id}/reconnect", async (
            HttpContext context,
            string id,
            RemoteRunnerLinkService links,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryAuthorize(context, configuration, out var denied, out _, out _)) return denied!;
            var result = await links.ReconnectAsync(id, ct);
            return result is null
                ? Results.Json(new { error = "runner-not-found" }, statusCode: 404)
                : result.Succeeded
                    ? Results.Ok(result)
                    : Results.Json(result, statusCode: 502);
        });
        group.MapPost("/remote-hosts/provider-auth", async (
            HttpContext context,
            ProviderAuthProvisioningRequest request,
            IProviderAuthProvisioner provisioner,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryAuthorize(context, configuration, out var denied, out _, out _)) return denied!;
            var validation = ProviderAuthProvisioningPolicy.Validate(request);
            if (validation is not null)
                return Results.Json(new { error = "invalid-provider-auth-request", message = validation }, statusCode: 400);
            try
            {
                return Results.Ok(await provisioner.ProvisionAsync(request, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.Json(
                    new { error = "invalid-provider-auth-request", message = ex.Message },
                    statusCode: 400);
            }
            catch (ProviderAuthProvisioningException ex)
            {
                return Results.Json(
                    new { error = "provider-auth-provisioning-failed", message = ex.Message },
                    statusCode: 502);
            }
        });
        group.MapPost("/commands", (HttpContext context, ManagementCommandRequest request, ManagementService service, IConfiguration configuration) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryAuthorize(context, configuration, out var denied, out var actor, out var role)) return denied!;
            try
            {
                return Results.Ok(service.Execute(
                    request, actor!, role!,
                    context.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? ""));
            }
            catch (ManagementException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
        });
    }

    private static bool TryAuthorize(
        HttpContext context, IConfiguration configuration, out IResult? denied,
        out string? actor, out string? role)
    {
        actor = null;
        role = null;
        if (SecurityProfiles.ActiveProfile(configuration) == SecurityProfiles.Networked)
        {
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal principal)
            {
                denied = Results.Json(
                    new
                    {
                        error = "authentication-required",
                        message = "Sign in with an owner or operator account to manage the Task Server.",
                        loginUrl = "/api/auth/login"
                    },
                    statusCode: StatusCodes.Status401Unauthorized);
                return false;
            }
            actor = principal.User.Id;
            role = principal.User.Role;
            if (role is not (StudioRoles.Owner or StudioRoles.Operator))
            {
                denied = Results.Json(
                    new
                    {
                        error = "management-role-required",
                        message = "Owner or operator role is required for Task Server management."
                    },
                    statusCode: StatusCodes.Status403Forbidden);
                return false;
            }
            denied = null;
            return true;
        }

        var headerClientId = context.Request.Headers["X-Client-Id"].FirstOrDefault();
        var attributedClientId = context.Items["ClientId"]?.ToString();
        var remoteAddress = context.Connection.RemoteIpAddress;
        // TestServer has no network peer and therefore reports no remote
        // address. Kestrel requests must carry an actual loopback address.
        var loopback = remoteAddress is null || System.Net.IPAddress.IsLoopback(remoteAddress);
        if (!loopback
            || !string.Equals(headerClientId, DefaultClientIdentity.Id, StringComparison.Ordinal)
            || !string.Equals(attributedClientId, DefaultClientIdentity.Id, StringComparison.Ordinal))
        {
            denied = Results.Json(
                new
                {
                    error = "local-operator-required",
                    message = "Local Task Server management requires the loopback local-default operator."
                },
                statusCode: StatusCodes.Status401Unauthorized);
            return false;
        }

        actor = DefaultClientIdentity.Id;
        role = StudioRoles.Operator;
        denied = null;
        return true;
    }
}

internal static class RecoveryConsole
{
    public const string Html = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Task Server recovery</title><style>
:root{color-scheme:light dark;font:15px system-ui;--bg:#111827;--card:#1f2937;--fg:#f9fafb;--muted:#9ca3af;--accent:#60a5fa;--bad:#f87171}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg)}main{padding:2rem;width:100%}header,section{background:var(--card);border:1px solid #374151;border-radius:12px;padding:1rem;margin-bottom:1rem}h1,h2{margin:.2rem 0 1rem}small,.muted{color:var(--muted)}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:1rem}dl{display:grid;grid-template-columns:auto 1fr;gap:.5rem}dd{margin:0;text-align:right;overflow-wrap:anywhere}button,input{font:inherit;padding:.65rem;border-radius:7px;border:1px solid #4b5563}button{background:var(--accent);color:#071526;font-weight:700;cursor:pointer}button.danger{background:var(--bad)}button:disabled{opacity:.5}input{background:transparent;color:inherit}.actions{display:flex;flex-wrap:wrap;gap:.5rem}.error{color:var(--bad)}pre{white-space:pre-wrap;overflow-wrap:anywhere} @media(prefers-color-scheme:light){:root{--bg:#f3f4f6;--card:#fff;--fg:#111827;--muted:#4b5563}}
</style></head><body><main><header><h1>Task Server bootstrap and recovery</h1><p class="muted">This console uses the authenticated management API. The operating system service manager owns process start and restart.</p></header>
<section id="auth"><h2>Owner session</h2><div class="actions"><input id="user" placeholder="Username"><input id="password" type="password" placeholder="Password"><button onclick="login()">Login</button><button onclick="bootstrap()">Create first owner</button></div><p id="authResult" class="muted"></p></section>
<section><div class="actions"><button onclick="load()">Refresh authoritative state</button><button onclick="diagnostics()">Recovery diagnostics</button></div><p id="error" class="error"></p></section>
<div class="grid"><section><h2>Health</h2><dl id="health"></dl></section><section><h2>Store</h2><dl id="store"></dl></section><section><h2>Credentials and Runners</h2><div class="actions"><input id="runnerName" placeholder="New Runner name"><button onclick="run('runner-enrollment-create',false,{runnerName:runnerName.value})">Preview enrollment</button></div><div id="runners"></div></section><section><h2>Backup and migration</h2><div id="backup"></div></section></div>
<section><h2>Audited commands</h2><p class="muted">Every button previews first. Confirming requires a second click and a fresh idempotency key.</p><div class="actions" id="actions"></div><pre id="result"></pre></section>
</main><script>
let csrf='';const commands=['backup-create','restore-verify','backup-retention','archive-sweep','orphan-sweep','fixture-sweep','maintenance-enter','maintenance-read-only','maintenance-exit','shutdown-prepare'];
const headers=()=>({'Content-Type':'application/json','X-Client-Id':'local-default',...(csrf?{'X-CSRF-Token':csrf}:{})});
async function call(path,options={}){const r=await fetch(path,{credentials:'same-origin',...options,headers:{...headers(),...(options.headers||{})}});const body=await r.json().catch(()=>({}));if(!r.ok)throw new Error(body.message||body.error||r.statusText);return body}
async function login(){return authenticate('/api/auth/login')}async function bootstrap(){return authenticate('/api/auth/bootstrap')}
async function authenticate(path){try{const body=await call(path,{method:'POST',body:JSON.stringify({username:user.value,password:password.value,displayName:user.value})});csrf=body.csrfToken||body.csrf||'';authResult.textContent='Authenticated. Refreshing management state.';await load()}catch(e){authResult.textContent=e.message}}
function esc(v){return String(v??'-').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]))}
function js(v){return JSON.stringify(String(v)).replace(/[<>&']/g,c=>'\\u'+c.charCodeAt(0).toString(16).padStart(4,'0'))}
function rows(obj,keys){return keys.map(k=>`<dt>${esc(k)}</dt><dd>${esc(obj?.[k])}</dd>`).join('')}
function runnerRow(x){const id=js(x.id);return `<p><b>${esc(x.displayName)}</b><br><small>${esc(x.state)}, last used ${esc(x.lastUsedAt||'never')}, active ${esc(x.activeSlots)}</small><br><button onclick='run("runner-drain",false,{runnerId:${id}})'>Preview drain</button> <button onclick='run("runner-retire",false,{runnerId:${id}})'>Preview retire</button> <button onclick='run("runner-credential-rotate",false,{runnerId:${id}})'>Preview credential rotation</button> <button class="danger" onclick='run("runner-revoke",false,{runnerId:${id}})'>Preview revoke</button></p>`}
async function load(){try{error.textContent='';const s=await call('/api/v1/management/status');health.innerHTML=rows({...s.server,...s.health},['id','url','version','protocolMinimum','protocolMaximum','uptimeSeconds','state','ready']);store.innerHTML=rows(s.store,['sizeBytes','projectCount','taskCount','archivedTaskCount','eventCount','artifactCount','identityCount']);runners.innerHTML=(s.runners||[]).map(runnerRow).join('')||'<p>No enrolled Runners.</p>';backup.innerHTML=`<p>Maintenance: <b>${esc(s.maintenance.mode)}</b></p><p>Backups: <b>${esc(s.backups.items.length)}</b>, failure: ${esc(s.backups.lastFailure||'none')}</p><p>Migrations: <b>${esc(s.migrations.length)}</b></p><p>Users: <a href="${esc(s.security.usersUrl)}">${esc(s.security.usersUrl)}</a><br>Runner credentials: <a href="${esc(s.security.runnerCredentialsUrl)}">${esc(s.security.runnerCredentialsUrl)}</a></p>`}catch(e){error.textContent=e.message}}
async function diagnostics(){try{result.textContent=JSON.stringify(await call('/api/v1/management/diagnostics'),null,2)}catch(e){error.textContent=e.message}}
async function run(kind,apply=false,extra={}){try{const key=crypto.randomUUID();const body={kind,dryRun:!apply,confirmation:apply?kind:null,idempotencyKey:key,...extra};const r=await call('/api/v1/management/commands',{method:'POST',headers:{'Idempotency-Key':key},body:JSON.stringify(body)});result.textContent=JSON.stringify(r,null,2);if(!apply){const b=document.createElement('button');b.className='danger';b.textContent=`Confirm ${kind}`;b.onclick=()=>{b.remove();run(kind,true,extra)};actions.appendChild(b)}else await load()}catch(e){error.textContent=e.message}}
commands.forEach(kind=>{const b=document.createElement('button');b.textContent=`Preview ${kind}`;b.onclick=()=>run(kind);actions.appendChild(b)});load();
</script></body></html>
""";
}
