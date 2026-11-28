namespace AgentStudio.TaskServer;

internal static class ModelCallSchema
{
    public const string Create = """
        CREATE TABLE IF NOT EXISTS model_call_import (
            workspace_id TEXT NOT NULL,
            project_id TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            first_received_ms INTEGER NOT NULL,
            actor_id TEXT NOT NULL,
            PRIMARY KEY (workspace_id, project_id, fingerprint)
        );
        CREATE TABLE IF NOT EXISTS model_call (
            id TEXT NOT NULL PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            project_id TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            task_id TEXT NOT NULL,
            card TEXT NOT NULL,
            run INTEGER NOT NULL,
            ordinal INTEGER NOT NULL,
            happened_at_ms INTEGER NOT NULL,
            current_revision INTEGER NOT NULL,
            UNIQUE (workspace_id, project_id, fingerprint, card, run, ordinal),
            FOREIGN KEY (workspace_id, project_id, fingerprint)
                REFERENCES model_call_import (workspace_id, project_id, fingerprint)
        );
        CREATE INDEX IF NOT EXISTS ix_model_call_scope_time
            ON model_call (workspace_id, project_id, happened_at_ms, id);
        CREATE INDEX IF NOT EXISTS ix_model_call_card_run_time
            ON model_call (workspace_id, project_id, card, run, happened_at_ms, id);
        CREATE TABLE IF NOT EXISTS model_call_revision (
            call_id TEXT NOT NULL REFERENCES model_call (id),
            revision INTEGER NOT NULL,
            received_ms INTEGER NOT NULL,
            kind TEXT NOT NULL,
            model TEXT,
            reasoning_level TEXT,
            service_tier TEXT,
            prompt_tokens INTEGER,
            fresh_input INTEGER,
            cached_input INTEGER,
            output INTEGER,
            reasoning INTEGER,
            total_tokens INTEGER,
            price_status TEXT NOT NULL,
            usd_nanos INTEGER,
            price_evidence TEXT,
            PRIMARY KEY (call_id, revision)
        );
        CREATE TABLE IF NOT EXISTS model_call_alias (
            workspace_id TEXT NOT NULL,
            project_id TEXT NOT NULL,
            provider TEXT NOT NULL,
            response_alias TEXT NOT NULL,
            call_id TEXT NOT NULL UNIQUE REFERENCES model_call (id),
            PRIMARY KEY (workspace_id, project_id, provider, response_alias)
        );
        CREATE TABLE IF NOT EXISTS coverage_receipt (
            workspace_id TEXT NOT NULL,
            project_id TEXT NOT NULL,
            source TEXT NOT NULL,
            receipt_key TEXT NOT NULL,
            task_id TEXT,
            run_id TEXT,
            from_ms INTEGER NOT NULL,
            until_ms INTEGER NOT NULL,
            granularity TEXT NOT NULL,
            fresh_input INTEGER,
            cached_input INTEGER,
            cache_write INTEGER,
            output INTEGER,
            PRIMARY KEY (workspace_id, project_id, source, receipt_key),
            CHECK (until_ms > from_ms)
        );
        """;
}
