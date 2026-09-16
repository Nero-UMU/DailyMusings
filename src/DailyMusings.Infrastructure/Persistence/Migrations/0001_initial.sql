-- 0001_initial — the whole phase-1 schema.
--
-- Conventions:
--   * Ids are UUID v7 stringified with "D" format.
--   * Instants are ISO-8601 round-trip strings in UTC; content days are "yyyy-MM-dd".
--   * Booleans are INTEGER 0/1; enums are INTEGER matching the domain enum values.
--
-- Where the product makes a promise, the schema enforces it rather than trusting the caller:
--   reflection.content_date UNIQUE          -- one reflection per content day (§7)
--   processing_job.idempotency_key UNIQUE   -- replayed work cannot enqueue twice (§14)
--   publication(version, target) UNIQUE     -- the same version cannot publish twice to one target (§14)
--   device.token_hash UNIQUE                -- a token identifies exactly one device (§10.2)

CREATE TABLE IF NOT EXISTS admin_account (
    id                         TEXT    NOT NULL PRIMARY KEY,
    username                   TEXT    NOT NULL UNIQUE,
    password_hash              TEXT    NOT NULL,
    must_change_password       INTEGER NOT NULL,
    created_at_utc             TEXT    NOT NULL,
    credentials_changed_at_utc TEXT    NULL
);

CREATE TABLE IF NOT EXISTS device (
    id               TEXT    NOT NULL PRIMARY KEY,
    name             TEXT    NOT NULL,
    token_hash       TEXT    NOT NULL UNIQUE,
    platform         TEXT    NULL,
    created_at_utc   TEXT    NOT NULL,
    last_seen_at_utc TEXT    NULL,
    revoked_at_utc   TEXT    NULL
);

-- Ten-minute single-use pairing codes (§10.2). Device tokens are deliberately NOT part of a backup, so
-- this table is expected to be empty after a restore (decision A.13).
CREATE TABLE IF NOT EXISTS pairing_code (
    id                 TEXT NOT NULL PRIMARY KEY,
    code_hash          TEXT NOT NULL UNIQUE,
    created_at_utc     TEXT NOT NULL,
    expires_at_utc     TEXT NOT NULL,
    used_at_utc        TEXT NULL,
    redeemed_device_id TEXT NULL REFERENCES device (id)
);

CREATE INDEX IF NOT EXISTS ix_pairing_code_expiry ON pairing_code (expires_at_utc);

CREATE TABLE IF NOT EXISTS topic (
    id             TEXT NOT NULL PRIMARY KEY,
    name           TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    -- Merge is a tombstone, never a delete (decision A.9): retired rows stay so that nothing that
    -- referenced the topic can dangle or be silently reused.
    merged_into_id TEXT NULL REFERENCES topic (id),
    merged_at_utc  TEXT NULL
);

CREATE TABLE IF NOT EXISTS input_entry (
    id                    TEXT    NOT NULL PRIMARY KEY,
    source_type           INTEGER NOT NULL,
    created_at_utc        TEXT    NOT NULL,
    created_offset_minutes INTEGER NOT NULL,
    content_date          TEXT    NOT NULL,
    audio_path            TEXT    NULL,
    audio_duration_ticks  INTEGER NULL,
    audio_deleted_at_utc  TEXT    NULL,
    original_transcript   TEXT    NULL,
    revised_transcript    TEXT    NULL,
    transcription_status  INTEGER NOT NULL,
    allow_future_recall   INTEGER NOT NULL,
    primary_topic_id      TEXT    NULL REFERENCES topic (id),
    deleted_at_utc        TEXT    NULL
);

CREATE INDEX IF NOT EXISTS ix_input_entry_content_date ON input_entry (content_date);
CREATE INDEX IF NOT EXISTS ix_input_entry_recall ON input_entry (allow_future_recall, content_date);

CREATE TABLE IF NOT EXISTS input_entry_secondary_topic (
    input_entry_id TEXT NOT NULL REFERENCES input_entry (id) ON DELETE CASCADE,
    topic_id       TEXT NOT NULL REFERENCES topic (id),
    PRIMARY KEY (input_entry_id, topic_id)
);

CREATE TABLE IF NOT EXISTS reflection (
    id                   TEXT    NOT NULL PRIMARY KEY,
    content_date         TEXT    NOT NULL UNIQUE,
    status               INTEGER NOT NULL,
    generation_reason    INTEGER NOT NULL,
    last_stale_reason    INTEGER NULL,
    initial_version_id   TEXT    NULL REFERENCES reflection_version (id),
    previous_version_id  TEXT    NULL REFERENCES reflection_version (id),
    working_version_id   TEXT    NULL REFERENCES reflection_version (id),
    confirmed_version_id TEXT    NULL REFERENCES reflection_version (id),
    created_at_utc       TEXT    NOT NULL,
    updated_at_utc       TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS reflection_version (
    id               TEXT    NOT NULL PRIMARY KEY,
    reflection_id    TEXT    NOT NULL REFERENCES reflection (id) ON DELETE CASCADE,
    title            TEXT    NOT NULL,
    summary          TEXT    NOT NULL,
    body             TEXT    NOT NULL,
    tags_json        TEXT    NOT NULL,
    categories_json  TEXT    NOT NULL,
    settings_json    TEXT    NOT NULL,
    model_name       TEXT    NULL,
    prompt_version   TEXT    NULL,
    has_manual_edits INTEGER NOT NULL,
    created_at_utc   TEXT    NOT NULL,
    edited_at_utc    TEXT    NULL
);

CREATE INDEX IF NOT EXISTS ix_reflection_version_reflection ON reflection_version (reflection_id);

-- Paragraph-level provenance (§6.5). quote_hash is what lets the client detect that an edited paragraph
-- no longer says what it said when the map was built (decision A.6).
CREATE TABLE IF NOT EXISTS source_reference (
    id                    TEXT    NOT NULL PRIMARY KEY,
    reflection_version_id TEXT    NOT NULL REFERENCES reflection_version (id) ON DELETE CASCADE,
    block_index           INTEGER NOT NULL,
    char_start            INTEGER NOT NULL,
    char_end              INTEGER NOT NULL,
    quote_hash            TEXT    NOT NULL,
    input_entry_id        TEXT    NOT NULL REFERENCES input_entry (id),
    relevance             REAL    NOT NULL,
    reason                TEXT    NOT NULL,
    is_historical         INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_source_reference_version ON source_reference (reflection_version_id);

-- Second-stage findings (§8.4): warnings the user must see, never a hard block.
CREATE TABLE IF NOT EXISTS unsourced_claim (
    id                    INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    reflection_version_id TEXT    NOT NULL REFERENCES reflection_version (id) ON DELETE CASCADE,
    block_index           INTEGER NOT NULL,
    char_start            INTEGER NOT NULL,
    char_end              INTEGER NOT NULL,
    reason                TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS processing_job (
    id                  TEXT    NOT NULL PRIMARY KEY,
    job_type            INTEGER NOT NULL,
    target_id           TEXT    NOT NULL,
    status              INTEGER NOT NULL,
    attempt_count       INTEGER NOT NULL,
    scheduled_at_utc    TEXT    NOT NULL,
    started_at_utc      TEXT    NULL,
    completed_at_utc    TEXT    NULL,
    idempotency_key     TEXT    NULL UNIQUE,
    error_code          TEXT    NULL,
    error_summary       TEXT    NULL,
    next_attempt_at_utc TEXT    NULL
);

CREATE INDEX IF NOT EXISTS ix_processing_job_due ON processing_job (status, scheduled_at_utc);

CREATE TABLE IF NOT EXISTS publish_target (
    id                              TEXT    NOT NULL PRIMARY KEY,
    name                            TEXT    NOT NULL UNIQUE,
    target_type                     INTEGER NOT NULL,
    destination_reference           TEXT    NULL,
    automatic_publish_enabled       INTEGER NOT NULL,
    automatic_publish_enabled_by    TEXT    NULL,
    automatic_publish_enabled_at_utc TEXT   NULL
);

CREATE TABLE IF NOT EXISTS publication (
    id                    TEXT    NOT NULL PRIMARY KEY,
    reflection_id         TEXT    NOT NULL REFERENCES reflection (id) ON DELETE CASCADE,
    reflection_version_id TEXT    NOT NULL REFERENCES reflection_version (id),
    publish_target_id     TEXT    NOT NULL REFERENCES publish_target (id),
    trigger_kind          INTEGER NOT NULL,
    status                INTEGER NOT NULL,
    remote_id             TEXT    NULL,
    attempt_count         INTEGER NOT NULL,
    scheduled_at_utc      TEXT    NOT NULL,
    triggered_by          TEXT    NULL,
    triggered_at_utc      TEXT    NULL,
    completed_at_utc      TEXT    NULL,
    error_code            TEXT    NULL,
    error_summary         TEXT    NULL,
    UNIQUE (reflection_version_id, publish_target_id)
);

CREATE INDEX IF NOT EXISTS ix_publication_due ON publication (status, scheduled_at_utc);

CREATE TABLE IF NOT EXISTS app_setting (
    setting_key    TEXT NOT NULL PRIMARY KEY,
    value          TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
