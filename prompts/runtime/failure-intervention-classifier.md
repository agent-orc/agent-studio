# Failure intervention classification

Classify this pipeline failure as exactly `PRODUCT` or `INFRASTRUCTURE`.
`PRODUCT` means a defect in the delivery. `INFRASTRUCTURE` means a provider,
toolchain, repository, network, host, or configuration failure.

- Code: `{{failure_code}}`
- Outcome: `{{outcome}}`
- Exit code: `{{exit_code}}`
- Duration ms: `{{duration_ms}}`
- Stdout/stderr: `{{command_evidence}}`

Return only one word.
