# Credential runbook replay fixtures

Redacted replay inputs for the three operator-reported incidents in
[docs/credentials-and-logins/runbooks.json](../../docs/credentials-and-logins/runbooks.json)
(AGT-2968). Each fixture holds typed predicate facts and the expected runbook
and recovery policy, nothing else.

These files are not measured historical logs. The provenance block records
`measured: false`: the facts restate the operator narrative, and fields the
narrative did not supply (incident ID, expiry, transcripts) are absent rather
than invented. No credential value, token fragment or key-shaped literal
appears here; tests that need a secret-shaped value build it at runtime.

`task-server.Tests/CredentialRunbookTests.cs` replays every case through the
selector and the durable executor.
