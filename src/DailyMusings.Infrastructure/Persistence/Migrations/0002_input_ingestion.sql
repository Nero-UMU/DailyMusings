-- 0002_input_ingestion — provenance for entries captured by a client device.
--
-- Why these columns exist:
--   client_idempotency_key  A client-generated key. §9.2 and §14 require an upload retry to be a no-op rather
--                           than a duplicate entry, and the client cannot know whether a timed-out request
--                           landed. The UNIQUE index below is what actually enforces that.
--   device_id               Which paired device captured the entry, so a client can render its own history
--                           without inventing a second identity system.
--   audio_content_type      The blob's MIME type, needed to hand it back to a model endpoint verbatim.

ALTER TABLE input_entry ADD COLUMN client_idempotency_key TEXT NULL;
ALTER TABLE input_entry ADD COLUMN device_id TEXT NULL REFERENCES device (id);
ALTER TABLE input_entry ADD COLUMN audio_content_type TEXT NULL;

-- Partial index on purpose: only rows that actually carry a key participate, so the many server-created
-- entries without one cannot collide with each other over NULL.
CREATE UNIQUE INDEX IF NOT EXISTS ux_input_entry_client_key
    ON input_entry (client_idempotency_key)
    WHERE client_idempotency_key IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_input_entry_device ON input_entry (device_id);

-- Transcription failures are surfaced per entry so a client can show "why is this one stuck" without joining
-- the job table; the authoritative retry state still lives in processing_job.
ALTER TABLE input_entry ADD COLUMN transcription_error_code TEXT NULL;
