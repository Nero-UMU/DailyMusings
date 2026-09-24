-- 0007_article_topics — articles now carry topics, and topics now remember where they came from.
--
-- Why this exists: until now a topic was only ever something an input was filed under, and only a human could
-- create one. The generation step now picks the topics an article is about (and may name a new one), so the
-- judgement "what was this day about" has to be stored per version rather than derived from the day's inputs:
-- a version is an immutable-in-spirit snapshot (§6.4), and its topics describe that snapshot, not the inputs
-- that happen to be filed under the same words today.
--
-- What this migration deliberately does NOT do:
--   * It does not backfill reflection_version_topic from inputs. A version's topics are a judgement about the
--     article; inventing one from the inputs would put a claim in the database that no one ever made. Existing
--     versions simply have no topics until they are regenerated, which the admin page shows honestly.
--   * It does not touch input_entry's own topic assignments, nor any source mapping.

-- Article topics: the topics one day's article was written about. is_primary marks the single "mainly about"
-- topic; the rest are secondary. Ordered by insertion, so the primary is always first for a reader who forgets
-- to filter on the flag.
CREATE TABLE IF NOT EXISTS reflection_version_topic (
    reflection_version_id TEXT    NOT NULL REFERENCES reflection_version (id) ON DELETE CASCADE,
    topic_id              TEXT    NOT NULL REFERENCES topic (id),
    is_primary            INTEGER NOT NULL,
    created_at_utc        TEXT    NOT NULL,
    PRIMARY KEY (reflection_version_id, topic_id)
);

-- The deletion guard reads this index: "is any article using this topic" has to be cheap, because it runs
-- before every topic deletion.
CREATE INDEX IF NOT EXISTS ix_reflection_version_topic_topic ON reflection_version_topic (topic_id);

-- Where a topic came from: 0 = created by the user, 1 = named by the model during generation.
-- Display and audit only — the two origins have exactly the same rights, and the deletion guard applies to
-- both. Default 0 is the honest value for every row that already exists: a human made them.
ALTER TABLE topic ADD COLUMN origin INTEGER NOT NULL DEFAULT 0;
