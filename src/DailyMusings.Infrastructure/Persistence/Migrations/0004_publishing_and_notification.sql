-- 0004_publishing_and_notification — what a publication needs in order to be verifiable and re-runnable.
--
-- Why these columns exist:
--   requested_visibility     What the request asked for. Without it, "the 08:00 slot uploaded a draft because the
--                            target never opted in" and "the slot published publicly" are the same row after the
--                            fact, and §11.1's rule cannot be audited.
--   published_content_hash   The hash of what was actually sent. §11.1 lets a user check for remote divergence and
--                            choose to pull, overwrite or keep both; without a record of what we sent, "the remote
--                            changed" and "our draft changed" cannot be told apart from "nothing happened", and
--                            the choice would be presented with no evidence behind it.
--   remote_content_hash      The hash the last remote check observed. Null means "never looked", which must never
--                            be confused with "in sync" — that confusion is how a silent overwrite happens.
--   remote_checked_at_utc    When that observation was made, so a stale comparison is visible as stale.
--   remote_divergence_ack..  Set when the user chose "keep both" (§11.1), so an accepted difference stops being
--                            reported as news on every later check.
--   export_round             How many times this version has gone to this target, which is what lets a re-export
--                            (§11.2) be a new job rather than a collision with the one that already succeeded.

ALTER TABLE publication ADD COLUMN requested_visibility INTEGER NOT NULL DEFAULT 0;
ALTER TABLE publication ADD COLUMN published_content_hash TEXT NULL;
ALTER TABLE publication ADD COLUMN remote_content_hash TEXT NULL;
ALTER TABLE publication ADD COLUMN remote_checked_at_utc TEXT NULL;

-- "Keep both" is a decision the user is allowed to make (§11.1), so it has to be recordable: without it the
-- difference would be rediscovered on every check and reported as if nobody had ever looked at it.
ALTER TABLE publication ADD COLUMN remote_divergence_acknowledged_at_utc TEXT NULL;

-- How many times this version has been sent to this target. Part of the job's idempotency key: without it, a
-- re-export would find the job that already succeeded and decline to run, and §11.2's "export again, as a new
-- versioned file" would be impossible to implement.
ALTER TABLE publication ADD COLUMN export_round INTEGER NOT NULL DEFAULT 0;

-- A remote check and a retry both look publications up by what they point at, and the nightly slot walks the
-- ones that are still queued.
CREATE INDEX IF NOT EXISTS ix_publication_reflection ON publication (reflection_id, status);
CREATE INDEX IF NOT EXISTS ix_publication_remote ON publication (publish_target_id, remote_id);
