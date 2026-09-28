-- An explicitly deleted article must not be recreated by the next scheduler tick while its source inputs remain.
CREATE TABLE IF NOT EXISTS deleted_reflection (
    content_date   TEXT NOT NULL PRIMARY KEY,
    deleted_at_utc TEXT NOT NULL
);
