-- 0006_markdown_only — the project's direction changed: there is no desktop client, and the only thing the
-- product delivers is a Markdown file (what Hexo consumes). WordPress is out of the product, so its targets and
-- the publication records that point at them go with it.
--
-- What this migration deliberately does NOT do:
--   * It does not touch reflections, versions, sources, inputs or media. Losing a blog connection must not cost
--     the user any of their own writing.
--   * It does not touch the Markdown targets' files. The files on disk were written by this instance and remain
--     exactly where they are; only the records that describe them are renumbered.
--
-- Order matters: `publication.publish_target_id` references `publish_target (id)` without ON DELETE CASCADE, so
-- the publication rows have to go first.

DELETE FROM publication
 WHERE publish_target_id IN (SELECT id FROM publish_target WHERE target_type = 0);

DELETE FROM publish_target WHERE target_type = 0;

-- The surviving rows were Markdown = 1; in the new enum Markdown is 0.
UPDATE publish_target SET target_type = 0 WHERE target_type = 1;

-- The per-target WordPress site overrides (§8.1) are settings rows, not a table: they are named
-- `publish.wordpress.<target id>.<field>` and nothing reads them any more.
DELETE FROM app_setting WHERE setting_key LIKE 'publish.wordpress.%';
