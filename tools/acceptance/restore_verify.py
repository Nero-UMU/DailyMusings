#!/usr/bin/env python3
"""§15.2's eight-step restore verification, run against a brand-new instance.

Two phases, because the staged restore is applied at the next start by design:

    restore_verify.py stage <archive.zip>   steps 1-3: fresh instance, admin initialized, archive uploaded
    restore_verify.py verify                steps 4-8: after the restart

The evidence comes from the real instance: its API, and its own state volume read through a
throwaway container so that the Markdown output and the secret scan are checked where they live.
"""

import hashlib
import io
import json
import os
import subprocess
import sys
import time
import urllib.request
import wave

from dmverify import Api, check, error_code, note, poll, results, step, summary_exit

BASE = os.environ.get("DM_BASE", "http://127.0.0.1:18321")
WORK = os.environ.get("DM_WORK", "")
VOLUME = os.environ.get("DM_VOLUME", "")
IMAGE = os.environ.get("DM_IMAGE", "dailymusings/server:local")
ADMIN_INITIAL_PASSWORD = os.environ.get("DM_ADMIN_INITIAL_PASSWORD", "")
ADMIN_USER = os.environ.get("DM_ADMIN_USER", "owner")
ADMIN_PASSWORD = os.environ.get("DM_ADMIN_PASSWORD", "CorrectHorseBattery1")


def sign_in(api, username, password):
    return api.post_form(
        "/api/admin/sign-in",
        {"username": username, "password": password, "acknowledgeRisk": "yes"},
    )


def inspect_state(needles, inspect_script):
    """Runs the inspection inside the instance's own image with its state volume mounted, so what is
    checked is the restored volume itself and not a copy of it."""
    mounts = ["-v", "%s:/state" % VOLUME, "-v", "%s:/inspect.sh:ro" % inspect_script]
    environment = []
    for index, needle in enumerate(needles):
        environment += ["-e", "NEEDLE_%d=%s" % (index, needle)]
    command = ["docker", "run", "--rm", "-u", "0"] + mounts + environment + [
        "--entrypoint", "/bin/sh", IMAGE, "/inspect.sh",
    ]
    completed = subprocess.run(command, capture_output=True, text=True, timeout=600)
    note("inspect stdout:\n%s" % completed.stdout.strip())
    if completed.returncode != 0:
        note("inspect stderr: %s" % completed.stderr.strip())
    values = {}
    for line in completed.stdout.splitlines():
        if "=" in line:
            key, _, value = line.partition("=")
            values[key.strip()] = value.strip()
    return values, completed.stdout


def stage(archive):
    evidence = json.load(open(os.path.join(WORK, "acceptance-evidence.json"), encoding="utf-8"))
    admin = Api(BASE)

    step("R2 (§15.2 step 2) a fresh instance starts, migrates and initializes its administrator")

    health = poll(lambda: admin.get("/api/system/health"), lambda r: r.status == 200, 180, 3.0, "health")
    payload = health.json() or {}
    check("the fresh instance answers its health check", payload.get("healthy") is True,
          json.dumps(payload, ensure_ascii=False)[:300])

    sign_in(admin, "admin", ADMIN_INITIAL_PASSWORD)
    change = admin.post_form(
        "/api/admin/credentials",
        {
            "currentPassword": ADMIN_INITIAL_PASSWORD,
            "newUsername": ADMIN_USER,
            "newPassword": ADMIN_PASSWORD,
            "confirmPassword": ADMIN_PASSWORD,
        },
    )
    check("the fresh instance's administrator is initialized and can be signed in",
          change.status in (200, 302), "credential change status=%s" % change.status)
    sign_in(admin, ADMIN_USER, ADMIN_PASSWORD)

    empty = admin.get("/api/reflections/%s" % evidence["today"])
    check("the fresh instance has no draft yet, so what appears later really came from the archive",
          empty.status == 404, "GET /api/reflections/%s -> %s" % (evidence["today"], empty.status))

    step("R3 (§15.2 step 3) the backup is uploaded and staged")

    with open(archive, "rb") as handle:
        content = handle.read()

    staged = admin.post_multipart("/api/backups/restore", {}, {"archive": (os.path.basename(archive), content, "application/zip")})
    staged_body = staged.json() or {}
    check("the archive is validated and accepted for restore",
          staged.status == 200 and staged_body.get("valid") is True,
          "status=%s schemaVersion=%s contents=%s" % (staged.status, staged_body.get("schemaVersion"), json.dumps(staged_body.get("contents"), ensure_ascii=False)[:300]))
    check("the verdict says what the archive contains before anything is applied",
          bool(staged_body.get("createdAtUtc")) and bool(staged_body.get("contents")),
          "createdAt=%s" % staged_body.get("createdAtUtc"))

    status = admin.get("/api/backups/restore").json() or {}
    check("the restore is reported as pending, because it is applied at the next start",
          status.get("pending") is True and status.get("valid") is True,
          json.dumps(status, ensure_ascii=False)[:300])

    return evidence


def verify(inspect_script):
    evidence = json.load(open(os.path.join(WORK, "acceptance-evidence.json"), encoding="utf-8"))
    admin = Api(BASE)
    device = Api(BASE)
    today = evidence["today"]

    step("R4 (§15.2 step 4) the restored instance is healthy and its queue is settled")

    sign_in(admin, ADMIN_USER, ADMIN_PASSWORD)
    health = admin.get("/api/system/health").json() or {}
    check("health is green on the instance that restored the archive",
          health.get("healthy") is True,
          json.dumps(health.get("probes"), ensure_ascii=False)[:400])

    jobs = admin.get("/api/jobs?limit=200").json() or {}
    items = jobs.get("items") or []
    running = [job for job in items if job.get("status") == "running"]
    succeeded = [job for job in items if job.get("status") == "succeeded"]
    check("the restored queue has no job left running (a restart must not strand work)",
          len(running) == 0, "jobs listed=%d running=%d" % (len(items), len(running)))
    check("the work that had already finished came back with the archive",
          len(succeeded) >= 8, "succeeded jobs restored=%d, types=%s" % (
              len(succeeded), sorted({job.get("jobType") for job in succeeded})))

    step("R5 (§15.2 step 5) draft, versions and source map")

    reflection = admin.get("/api/reflections/%s" % today).json() or {}
    working = reflection.get("workingVersion") or {}
    check("the day's draft opens on the restored instance",
          reflection.get("status") == "confirmed" and bool(working.get("body")),
          "status=%s bodyLength=%d" % (reflection.get("status"), len(working.get("body") or "")))
    check("all three version slots are intact (initial, previous, working)",
          all(reflection.get(slot) for slot in ("initialVersionId", "previousVersionId", "workingVersionId"))
          and reflection.get("initialVersion") and reflection.get("previousVersion") and reflection.get("workingVersion"),
          "initial=%s previous=%s working=%s" % (reflection.get("initialVersionId"), reflection.get("previousVersionId"), reflection.get("workingVersionId")))
    check("the hand-edited version is still there as the previous slot",
          (reflection.get("previousVersion") or {}).get("hasManualEdits") is True,
          "previous hasManualEdits=%s" % (reflection.get("previousVersion") or {}).get("hasManualEdits"))

    sources = working.get("sources") or []
    body = working.get("body") or ""
    blocks = body.replace("\r\n", "\n").split("\n\n")
    slices = []
    for source in sources:
        block_index = source.get("blockIndex") or 0
        if block_index < len(blocks):
            slices.append(blocks[block_index].strip("\n")[source.get("charStart"):source.get("charEnd")])
    check("every source mapping resolves against the restored text, which is what QuoteHash matching means",
          len(sources) >= 2 and all(source.get("drift") == "exact" for source in sources) and all(text.strip() for text in slices),
          "%d sources, drifts=%s, first slices=%s" % (len(sources), sorted({source.get("drift") for source in sources}), json.dumps(slices[:2], ensure_ascii=False)[:200]))
    check("the historical citation survived the round trip",
          any(source.get("isHistorical") for source in sources),
          "historical=%d" % len([source for source in sources if source.get("isHistorical")]))

    step("R6 (§15.2 step 6) the recording plays and both transcripts are present")

    entry_id = (evidence.get("entryIds") or [None])[0]
    # A device token from the old instance is exactly what step 8 forbids, so read the audio as the administrator.
    audio = admin.get("/api/inputs/%s/audio" % entry_id)
    decoded = None
    try:
        with wave.open(io.BytesIO(audio.body), "rb") as handle:
            decoded = "%d ch, %d Hz, %.2fs" % (handle.getnchannels(), handle.getframerate(), handle.getnframes() / handle.getframerate())
    except Exception as exception:  # noqa: BLE001 - a failure to decode is the finding
        decoded = "decode failed: %s" % exception
    fixture_hash = (evidence.get("audioFixtureSha256") or {}).get("0")
    check("the restored audio is byte-identical to the recording and decodes as playable audio",
          audio.status == 200 and hashlib.sha256(audio.body).hexdigest() == fixture_hash,
          "status=%s bytes=%d sha256 match=%s decoded=%s" % (
              audio.status, len(audio.body), hashlib.sha256(audio.body).hexdigest() == fixture_hash, decoded))

    revised_id = (evidence.get("entryIds") or [None, None])[1]
    entry = admin.get("/api/inputs/%s" % revised_id).json() or {}
    check("both the original transcript and the user's revision came back",
          entry.get("originalTranscript") == evidence.get("originalTranscript")
          and entry.get("revisedTranscript") == evidence.get("revisedTranscript"),
          "original kept=%s revised kept=%s" % (
              entry.get("originalTranscript") == evidence.get("originalTranscript"),
              entry.get("revisedTranscript") == evidence.get("revisedTranscript")))

    step("R7 (§15.2 step 7) no secret in the export or the backup, and the files are all there")

    export = admin.post_json("/api/exports", {})
    check("an export can be taken on the restored instance", export.status == 200,
          "status=%s root=%s" % (export.status, (export.json() or {}).get("relativeRoot")))

    token_hash = hashlib.sha256((evidence.get("deviceToken") or "").encode("utf-8")).hexdigest()
    values, _output = inspect_state([token_hash], inspect_script)
    check("the state volume carries the restored Markdown, the media and a new export",
          int(values.get("MARKDOWN_FILES", "0")) >= 1 and int(values.get("MEDIA_FILES", "0")) >= 3
          and int(values.get("EXPORT_DIRS", "0")) >= 1,
          "markdown=%s media=%s backups=%s exports=%s" % (
              values.get("MARKDOWN_FILES"), values.get("MEDIA_FILES"), values.get("BACKUP_FILES"), values.get("EXPORT_DIRS")))
    check("the restored instance's backup directory is empty, because a backup does not contain backups",
          values.get("BACKUP_FILES") == "0", "backup archives on the restored instance=%s" % values.get("BACKUP_FILES"))
    check("no device token survives anywhere in the restored state or in a new export (A.13)",
          values.get("TOKEN_HASH_FOUND") == "0",
          "the pre-restore token's sha256 appears anywhere under the instance root: %s" % values.get("TOKEN_HASH_FOUND"))
    check("the export taken on the restored instance holds its files",
          int(values.get("EXPORT_FILES", "0")) >= 4 and values.get("EXPORT_AUDIO_FILES") is not None,
          "export files=%s, of which audio=%s" % (values.get("EXPORT_FILES"), values.get("EXPORT_AUDIO_FILES")))

    step("R8 (§15.2 step 8) the old device token is dead and re-pairing works")

    old_token = evidence.get("deviceToken")
    stale = Api(BASE, token=old_token)
    rejected = stale.get("/api/inputs")
    check("the pre-restore device token no longer authenticates",
          rejected.status == 401, "GET /api/inputs with the old token -> %s" % rejected.status)

    devices = admin.get("/api/devices").json() or []
    check("the restored instance starts with no paired device at all",
          all(not item.get("revoked") for item in devices) and len(devices) == 0,
          "devices=%s" % json.dumps(devices, ensure_ascii=False)[:200])

    pairing = admin.post_json("/api/pairing/codes", {}).json() or {}
    redeem = device.post_json(
        "/api/pairing/redeem",
        {"code": pairing.get("code"), "deviceName": "re-paired-device", "platform": "android"},
    )
    device.token = (redeem.json() or {}).get("token")
    check("a new pairing code can be redeemed on the restored instance",
          redeem.status == 200 and bool(device.token), "status=%s" % redeem.status)

    capture = device.post_json(
        "/api/inputs/text",
        {"text": "恢复之后重新配对，写下这一条。", "createdAtUtc": None, "createdOffsetMinutes": 0, "idempotencyKey": "after-restore-1"},
    )
    check("the re-paired device can capture again", capture.status == 200,
          "status=%s contentDate=%s" % (capture.status, ((capture.json() or {}).get("input") or {}).get("contentDate")))

    listed = device.get("/api/inputs").json() or {}
    check("the re-paired device sees the restored history and its new capture",
          len(listed.get("items") or []) >= 5, "entries visible=%d" % len(listed.get("items") or []))

    return evidence


if __name__ == "__main__":
    # The inspection runs inside the instance's own image with the state volume mounted; the script lives next to
    # this one, and a missing file would silently mount an empty directory instead of failing.
    inspect_script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "inspect-state.sh")
    phase = sys.argv[1] if len(sys.argv) > 1 else ""
    try:
        if phase == "stage":
            stage(sys.argv[2])
        elif phase == "verify":
            check("the state inspection script is present", os.path.isfile(inspect_script), inspect_script)
            verify(inspect_script)
        else:
            raise SystemExit("usage: restore_verify.py stage <archive.zip> | verify")
    except SystemExit:
        raise
    except Exception as exception:  # noqa: BLE001 - an unexpected error is a failed run, never a quiet one
        import traceback

        traceback.print_exc()
        check("the restore verification reached the end without an unexpected error", False, repr(exception))
    finally:
        summary_exit(WORK, "restore-report.json")
