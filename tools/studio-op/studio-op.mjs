#!/usr/bin/env node
// studio-op: the official operator CLI of Agent Studio (Dossier
// operator-agent-level, decision OA-D1 option B). Setup and exit codes:
// docs/operations/setup/studio-op.md
import { main } from './src/cli.mjs';

process.exitCode = await main(process.argv.slice(2));
