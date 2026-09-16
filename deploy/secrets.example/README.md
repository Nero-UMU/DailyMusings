# Example secret files.
#
# Copy this directory to deploy/secrets/ and put the real values in. One file per secret, contents only — no
# quoting, and no trailing commentary. Docker mounts each file at /run/secrets/<name> and trims a trailing
# newline.
#
#   cp -r deploy/secrets.example deploy/secrets
#
# deploy/secrets/ is gitignored. These four files are placeholders and must not be used as-is.

openai-api-key                       # OpenAI-compatible transcription / generation API key
embedding-api-key                    # optional OpenAI-compatible embedding API key
smtp-password                        # SMTP password for the notification sender
wordpress-application-password       # WordPress application password used by the REST API
