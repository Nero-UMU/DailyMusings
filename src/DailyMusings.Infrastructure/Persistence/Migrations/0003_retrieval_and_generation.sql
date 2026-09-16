-- 0003_retrieval_and_generation — the state phase three needs: the embedding index, the marker that says
-- whether a version's sources were checked, and a parameter slot on the job queue.
--
-- Why each piece exists:
--   input_embedding        §8.3 indexes recallable entries so history retrieval can run semantically. The
--                          index is bound to an EmbeddingConfigVersion fingerprint, and A.8 decided to keep
--                          no second generation of it: rows carry the fingerprint they were built for, the
--                          switch to a new index is a single settings write, and the old rows are dropped.
--   text_hash              Hash of the text that was embedded. An edited transcript must be re-embedded, and
--                          the index job uses this to skip work that is still current.
--   sources_checked_at_utc §8.4 requires a second-stage unsourced-statement check. Without this marker an
--                          empty unsourced_claim table is ambiguous — "checked and clean" and "never checked"
--                          look identical, and the client would present the first when it means the second.
--   processing_job.payload_json
--                          Some work cannot be described by its target alone. Regenerating a day has to know
--                          whether the user already accepted losing a hand-edited working version (§6.4), and
--                          that decision must survive a restart exactly like the job does.

CREATE TABLE IF NOT EXISTS input_embedding (
    input_entry_id TEXT    NOT NULL REFERENCES input_entry (id) ON DELETE CASCADE,
    config_version TEXT    NOT NULL,
    model_name     TEXT    NOT NULL,
    dimensions     INTEGER NOT NULL,
    vector         BLOB    NOT NULL,
    text_hash      TEXT    NOT NULL,
    created_at_utc TEXT    NOT NULL,
    PRIMARY KEY (input_entry_id, config_version)
);

-- Retrieval always filters by fingerprint, so it leads the index.
CREATE INDEX IF NOT EXISTS ix_input_embedding_config ON input_embedding (config_version);

ALTER TABLE reflection_version ADD COLUMN sources_checked_at_utc TEXT NULL;

-- The check reads findings by version; without this the lookup is a full scan per draft.
CREATE INDEX IF NOT EXISTS ix_unsourced_claim_version ON unsourced_claim (reflection_version_id);

ALTER TABLE processing_job ADD COLUMN payload_json TEXT NULL;
