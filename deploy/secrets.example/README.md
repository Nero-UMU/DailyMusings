# Example secret files.
#
# Copy this directory to deploy/secrets/ and put the real values in. One file per secret, contents only — no
# quoting, and no trailing commentary. Docker mounts each file at /run/secrets/<name> and trims a trailing
# newline.
#
#   cp -r deploy/secrets.example deploy/secrets
#   chmod 700 deploy/secrets        # the directory stays the operator's
#   chmod 644 deploy/secrets/*      # ...but the files must be readable inside the container
#
# THE PERMISSIONS MATTER. A file secret is a bind mount, so the container reads it with the host's mode bits —
# and the container does not run as you, it runs as uid 1654 ("app"). A 600 file owned by the host user is
# therefore unreadable inside the container, and every model call then fails with `transcription.secret_missing`
# / `embedding.secret_missing` even though the file is right there under the right name. Found the hard way on a
# real deployment; 644 costs nothing here because the directory itself is 700.
#
# deploy/secrets/ is gitignored. These three files are placeholders and must not be used as-is.

openai-api-key                       # OpenAI-compatible transcription / generation API key
embedding-api-key                    # optional OpenAI-compatible embedding API key
smtp-password                        # SMTP password for the notification sender
