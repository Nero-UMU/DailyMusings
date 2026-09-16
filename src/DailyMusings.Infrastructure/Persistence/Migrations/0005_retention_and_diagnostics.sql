-- 0005_retention_and_diagnostics — the two pieces of instance state phase five needs.
--
-- Why this column exists:
--   reflection.confirmed_at_utc   Decision A.1 makes audio cleanable only after 转写成功且用户首次确认该日草稿, and the
--                                retention window is counted from that confirmation. Nothing else in the schema
--                                records when a human signed a day off: updated_at_utc moves for every later change
--                                (a stale mark, a regeneration), so counting from it would restart the clock each
--                                time the draft was touched and the recordings would never expire. It is the
--                                *first* confirmation that is kept, because that is the moment the audio stopped
--                                being the only evidence of what was said.
--
-- Nothing else is needed for §16's diagnostic mode: it is a temporary switch with an expiry, so it lives in the
-- settings table (keys `diagnostics.enabledUntil` and `diagnostics.enabledBy`) rather than acquiring a schema of
-- its own.

ALTER TABLE reflection ADD COLUMN confirmed_at_utc TEXT NULL;

-- The cleanup sweep walks confirmed days looking for expired audio, so the confirmation instant is what it filters
-- and sorts by.
CREATE INDEX IF NOT EXISTS ix_reflection_confirmed_at ON reflection (confirmed_at_utc);
