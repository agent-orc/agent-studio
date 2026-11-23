# Library releases

A package release runs the repository's own script. It needs no deploy host or
environment. Deploy > Releases lists deployable artifacts; use the Deck's
**Package and workflow publishing actions** for npm, NuGet, and library releases.

Add a command and branch to `.agent-studio/project.yml` alongside your normal
prepare, build, test, and lint commands:

```yaml
release:
  command: bash scripts/release.sh "$RELEASE_VERSION"
  branch: main
```

The existing `release.identity` and `release.restore` fields describe Studio's
Stable dependency identity contract. They remain supported and can coexist with
this command; they are not required for a library release command.

Keep the release branch current before releasing. Studio does not promote or
merge the integration branch automatically. The release branch must exist locally,
contain the same release definition, and be available for a temporary checkout.
If it is checked out elsewhere, Git refuses the release and names that checkout.

Open the publishing actions on the Deck and select the package. Choose the next
version (the first release defaults to `0.1.0`). **Dry run** shows the branch,
commit, verification commands, release command, and expected tag without executing
commands, changing files, or contacting the remote.

**Verify and release** creates a temporary checkout on the declared branch and
runs prepare, build, test, and lint in order. Build and test commands are required.
A failed command prevents the release script from running. Studio sets
`RELEASE_VERSION` to the selected semantic version and passes the configured
project environment to each command. The script owns version changes, including
lockfiles, its release commit, and the `vX.Y.Z` tag. It should leave pushing to
Studio, which checks that the tag points at the release branch tip and pushes the
branch and tag atomically to `origin`. Keep registry credentials in the host's
existing credential setup; do not add them to project.yml.

The temporary checkout is removed afterwards. Successful releases append the tag
and branch to the timeline of pending accepted cards contained in that tag.
The server's integration checkout remains on its original branch. Verification
and release failures are shown in the publishing panel; check local and remote
refs before retrying a script that performs external side effects.

Existing GitHub tag workflow targets remain supported. Their legacy action
requires the server checkout on `main`, updates npm manifest and lockfile root
versions together, and pushes explicitly to that branch. Declare a release
command to run from a different server checkout or release branch.

See [project onboarding](onboard-a-project.md) for the build and test definition.
