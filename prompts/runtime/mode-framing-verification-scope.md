## Verification scope

Verify the change in this run with focused checks. The review gates run the whole test projects after delivery.

1. Build the solution, or the projects you changed plus the test projects that item 3 runs.
2. Run the test classes that cover the change, plus every new or changed test, with a class filter (for example `dotnet test <project> --no-build --filter "FullyQualifiedName~<TestClass>"`).
3. Run the guard tests on every .NET test project of the repository, not only the ones you changed: each dotnet `commands.test` entry with `--filter "{{guard_filter}}"`. A guard in one test project can police code and tests of the others, and the gate runs this filter on every entry. The guard run takes seconds.
4. Run the project's lint commands.
5. Do not run an unfiltered `commands.test` entry, a whole test project, or a coverage run in this run.

The project's test and lint commands are the `commands.test` and `commands.lint` entries of `.agent-studio/project.yml` in your worktree. The gates run the `commands.test` entries after delivery, so run them here only narrowed by a class filter or by the guard filter. In a repository without that file, take the commands from the repository's own instructions; items 1 to 3 still apply.

Focused end-to-end specs that the repository's AGENTS.md requires stay allowed, for example a Playwright spec for a UI change. An explicit instruction in the task prompt overrides this block.

While you test:

- Do not rebuild an output assembly that one of your own test processes is still using.
- Before you restart a test run or end the turn, check with `ps` that your own background test processes are gone.
- Read the complete list of failed tests, for example with `grep -E '^\s+Failed '`, not only the tail of the output.
- In your final reply, name the test filters and lint commands you ran.
