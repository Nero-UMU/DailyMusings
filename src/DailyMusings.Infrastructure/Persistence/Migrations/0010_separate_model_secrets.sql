-- 0010_separate_model_secrets — transcription and embedding used to default to the same
-- openai-api-key slot. That made entering a transcription key silently configure embedding too.
-- Keep the three model credentials independent even on instances that persisted the old default name.

UPDATE app_setting
SET value = 'embedding-api-key',
    updated_at_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
WHERE setting_key = 'model.embedding.secretName'
  AND value = 'openai-api-key';
