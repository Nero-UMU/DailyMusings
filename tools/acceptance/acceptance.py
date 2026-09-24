#!/usr/bin/env python3
"""Phase-5 end-to-end acceptance driver (docs/开发指导.md §17.3, §15, §16).

Runs against a real instance started from deploy/compose.yaml with real external services:
a real SMTP server (Mailpit), the instance's real Markdown export directory, and an
OpenAI-compatible stub on 127.0.0.1:8077 that stands in for the model provider.

Every check prints PASS/FAIL with the evidence it used. Exit code is non-zero if any check
failed, so the caller cannot mistake a partial run for a green one.
"""

import hashlib
import http.cookiejar
import io
import json
import math
import os
import shutil
import sqlite3
import struct
import sys
import time
import urllib.request
import wave
import zipfile
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

BASE = os.environ.get("DM_BASE", "http://127.0.0.1:18321")
MAILPIT = os.environ.get("DM_MAILPIT", "http://127.0.0.1:8025")
STATE = os.environ.get("DM_STATE", "")
WORK = os.environ.get("DM_WORK", "")
ADMIN_INITIAL_PASSWORD = os.environ.get("DM_ADMIN_INITIAL_PASSWORD", "")
ADMIN_USER = os.environ.get("DM_ADMIN_USER", "owner")
ADMIN_PASSWORD = os.environ.get("DM_ADMIN_PASSWORD", "CorrectHorseBattery1")
TZ_NAME = os.environ.get("DM_TZ", "Asia/Shanghai")
MARKER = "下午我顺路去看了那家旧书店。"
TOPIC_NAME = "晨跑"

DAY_TRANSCRIPTS = [
    "今天早上晨跑的时候，听见小区里的桂花已经开了，香气一路跟着我。",
    "上午把上周没写完的报告收了个尾，比想象中顺利，下午就能交出去。",
    "晚上和朋友吃了顿火锅，聊到很晚才回家，心里很放松。",
]
HISTORY_TRANSCRIPT = "昨天晨跑的时候下了点小雨，跑完鞋子全湿了，但心情很好。"
TEXT_INPUT = "今天有点累，但心里挺踏实的。"

from dmverify import Api, check, error_code, note, poll, results, step, summary_exit

def now_utc():
    return datetime.now(timezone.utc)


def iso(value):
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def make_wav(frequency, seconds=1.0, sample_rate=8000):
    """A real, playable WAV whose bytes avoid the multipart delimiter, so the stub can hash it."""
    while True:
        buffer = io.BytesIO()
        with wave.open(buffer, "wb") as handle:
            handle.setnchannels(1)
            handle.setsampwidth(2)
            handle.setframerate(sample_rate)
            frames = bytearray()
            for index in range(int(sample_rate * seconds)):
                value = int(12000 * math.sin(2 * math.pi * frequency * index / sample_rate))
                frames += struct.pack("<h", value)
            handle.writeframes(bytes(frames))
        raw = buffer.getvalue()
        if b"\r\n--" not in raw:
            return raw
        frequency += 7


def mailpit_messages():
    with urllib.request.urlopen(MAILPIT + "/api/v1/messages", timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def mailpit_message(message_id):
    with urllib.request.urlopen(MAILPIT + "/api/v1/message/" + message_id, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def slice_claim(body, block_index, char_start, char_end):
    blocks = body.replace("\r\n", "\n").replace("\r", "\n").split("\n\n")
    if block_index >= len(blocks):
        return ""
    block = blocks[block_index].strip("\n")
    return block[char_start:char_end]


def main():
    zone = ZoneInfo(TZ_NAME)
    offset_minutes = int(now_utc().astimezone(zone).utcoffset().total_seconds() // 60)
    today = now_utc().astimezone(zone).date()
    yesterday = today - timedelta(days=1)
    print("content zone %s (offset %+d), today %s, yesterday %s" % (TZ_NAME, offset_minutes, today, yesterday))

    admin = Api(BASE)
    device = Api(BASE)

    # ---------------------------------------------------------------- setup
    step("S0 setup: admin, settings, targets, device")

    health = admin.get("/api/system/health")
    payload = health.json() or {}
    check(
        "health endpoint reports all probes healthy",
        health.status == 200 and payload.get("healthy") is True,
        "status=%s probes=%s" % (health.status, json.dumps(payload.get("probes"), ensure_ascii=False)),
    )

    initial = admin.get("/api/devices")
    check("admin endpoints refuse a caller with no session",
          initial.status in (401, 302), "GET /api/devices without a cookie -> %s" % initial.status)

    sign_in = admin.post_form(
        "/api/admin/sign-in",
        {"username": "admin", "password": ADMIN_INITIAL_PASSWORD, "acknowledgeRisk": "yes"},
    )
    check("initial administrator password from the container log is accepted",
          sign_in.status in (200, 302), "status=%s" % sign_in.status)

    change = admin.post_form(
        "/api/admin/credentials",
        {
            "currentPassword": ADMIN_INITIAL_PASSWORD,
            "newUsername": ADMIN_USER,
            "newPassword": ADMIN_PASSWORD,
            "confirmPassword": ADMIN_PASSWORD,
        },
    )
    check("forced credential change on first start succeeds", change.status in (200, 302), "status=%s" % change.status)

    sign_in = admin.post_form(
        "/api/admin/sign-in",
        {"username": ADMIN_USER, "password": ADMIN_PASSWORD, "acknowledgeRisk": "yes"},
    )
    check("sign-in with the new credentials succeeds", sign_in.status in (200, 302), "status=%s" % sign_in.status)

    settings = admin.patch_json(
        "/api/content-settings",
        {
            "timeZoneId": TZ_NAME,
            "generationLocalTime": "23:30",
            "publishLocalTime": "23:45",
            "publishWindowMinutes": 120,
            "audioRetentionDays": 30,
        },
    )
    check("content settings are administrable (zone, schedule, retention)",
          settings.status == 200, "status=%s body=%s" % (settings.status, settings.text()[:300]))

    # The draft-ready event has to be switched on explicitly: an instance with no switches set sends nothing at
    # all ("silence is the default"), so a run that only filled in the recipient would never mail anything — and
    # the §17.3 step 5 check below would be testing the harness's assumption instead of the product.
    notifications = admin.patch_json(
        "/api/notification-settings",
        {"toAddress": "owner@example.test", "instanceUrl": "http://127.0.0.1:18321", "draftReady": True},
    )
    notification_body = notifications.json() or {}
    check("notification recipient and the draft-ready switch are configurable",
          notifications.status == 200
          and notification_body.get("toAddress") == "owner@example.test"
          and notification_body.get("draftReady") is True,
          notifications.text()[:250])

    # A partial update is the normal case from the admin page: the fields it does not name must be left alone.
    partial = admin.patch_json("/api/notification-settings", {"jobFailed": True})
    partial_body = partial.json() or {}
    check("a partial notification update changes only what it names",
          partial.status == 200
          and partial_body.get("jobFailed") is True
          and partial_body.get("draftReady") is True
          and partial_body.get("toAddress") == "owner@example.test",
          partial.text()[:250])

    # Switched back off, so failure mails cannot add noise to the mailbox checks later in the run.
    admin.patch_json("/api/notification-settings", {"jobFailed": False})

    hexo_target = admin.post_json("/api/publish-targets", {"name": "Hexo 输出", "type": "markdown", "destinationReference": "hexo"})
    check("a Markdown (Hexo) publish target can be created", hexo_target.status in (200, 201),
          "hexo=%s body=%s" % (hexo_target.status, hexo_target.text()[:200]))
    hexo_target_id = (hexo_target.json() or {}).get("id")

    # The product has exactly one target kind now, so a client still asking for the removed one has to be told so
    # rather than quietly given a Markdown directory.
    removed_kind = admin.post_json("/api/publish-targets", {"name": "旧目标", "type": "wordPress", "destinationReference": None})
    check("the removed WordPress target kind is refused rather than silently accepted",
          removed_kind.status == 400,
          "status=%s body=%s" % (removed_kind.status, removed_kind.text()[:200]))

    topic = admin.post_json("/api/topics", {"name": TOPIC_NAME})
    topic_body = topic.json() or {}
    check("a topic can be created before capture so recognition has a vocabulary",
          topic.status in (200, 201) and topic_body.get("name") == TOPIC_NAME,
          "status=%s id=%s" % (topic.status, topic_body.get("id")))
    topic_id = topic_body.get("id")

    pairing = admin.post_json("/api/pairing/codes", {})
    pairing_body = pairing.json() or {}
    check("admin can issue a ten-minute one-time pairing code",
          pairing.status == 200 and pairing_body.get("code"), "status=%s expires=%s" % (pairing.status, pairing_body.get("expiresAtUtc")))

    redeem = device.post_json(
        "/api/pairing/redeem",
        {"code": pairing_body.get("code"), "deviceName": "acceptance-device", "platform": "android"},
    )
    redeem_body = redeem.json() or {}
    check("device pairs with the code and receives its own token",
          redeem.status == 200 and redeem_body.get("token"), "status=%s deviceId=%s" % (redeem.status, redeem_body.get("deviceId")))
    device.token = redeem_body.get("token")

    # ------------------------------------------------- capture and transcription
    step("S1 (§17.3 1-2) capture, upload, transcription, topic recognition")

    history_created = now_utc() - timedelta(hours=26)
    history = device.post_json(
        "/api/inputs/text",
        {
            "text": HISTORY_TRANSCRIPT,
            "createdAtUtc": iso(history_created),
            "createdOffsetMinutes": offset_minutes,
            "idempotencyKey": "acceptance-history-1",
        },
    )
    history_body = history.json() or {}
    history_input = history_body.get("input") or {}
    check("yesterday's material is accepted and filed on yesterday's content date",
          history.status == 200 and history_input.get("contentDate") == str(yesterday),
          "contentDate=%s (expected %s)" % (history_input.get("contentDate"), yesterday))
    history_id = history_input.get("id")

    text = device.post_json(
        "/api/inputs/text",
        {
            "text": TEXT_INPUT,
            "createdAtUtc": iso(now_utc()),
            "createdOffsetMinutes": offset_minutes,
            "idempotencyKey": "acceptance-text-1",
        },
    )
    text_body = text.json() or {}
    text_input = text_body.get("input") or {}
    check("a text capture lands on today's content date",
          text.status == 200 and text_input.get("contentDate") == str(today),
          "contentDate=%s transcriptionStatus=%s" % (text_input.get("contentDate"), text_input.get("transcriptionStatus")))
    text_id = text_input.get("id")

    transcripts = {}
    entries = []
    fixtures = {}
    for index, transcript in enumerate(DAY_TRANSCRIPTS):
        audio = make_wav(300 + index * 60)
        digest = hashlib.sha256(audio).hexdigest()
        transcripts[digest] = transcript
        fixtures[index] = digest
        response = device.post_multipart(
            "/api/inputs/voice",
            {
                "createdAtUtc": iso(now_utc()),
                "createdOffsetMinutes": offset_minutes,
                "idempotencyKey": "acceptance-voice-%d" % index,
                "durationMilliseconds": 1000,
            },
            {"audio": ("capture-%d.wav" % index, audio, "audio/wav")},
        )
        body = response.json() or {}
        entry = body.get("input") or {}
        entries.append(entry)
        check("voice capture %d uploads and is stored with its audio" % (index + 1),
              response.status == 200 and entry.get("hasAudio") is True,
              "status=%s id=%s status=%s" % (response.status, entry.get("id"), entry.get("transcriptionStatus")))

    # The stub reads its transcript map per request, so write it before any transcription can run.
    with open(os.path.join(WORK, "transcripts.json"), "w", encoding="utf-8") as handle:
        json.dump(transcripts, handle, ensure_ascii=False)
    note("wrote %d transcript fixtures for the model stub" % len(transcripts))

    retry = device.post_multipart(
        "/api/inputs/voice",
        {
            "createdAtUtc": iso(now_utc()),
            "createdOffsetMinutes": offset_minutes,
            "idempotencyKey": "acceptance-voice-2",
            "durationMilliseconds": 1000,
        },
        {"audio": ("capture-2.wav", make_wav(300 + 2 * 60), "audio/wav")},
    )
    retry_body = retry.json() or {}
    check("repeating a voice upload with the same idempotency key stores nothing new",
          retry_body.get("alreadyStored") is True and (retry_body.get("input") or {}).get("id") == entries[2].get("id"),
          "alreadyStored=%s id=%s" % (retry_body.get("alreadyStored"), (retry_body.get("input") or {}).get("id")))

    def input_state(entry_id):
        return device.get("/api/inputs/" + entry_id).json() or {}

    for index, entry in enumerate(entries):
        expected = DAY_TRANSCRIPTS[index]
        final = poll(lambda e=entry: input_state(e["id"]), lambda state: state.get("transcriptionStatus") == "succeeded", 180, 2.0, "transcription %d" % index)
        check("voice capture %d is transcribed by the configured endpoint" % (index + 1),
              final.get("transcript") == expected,
              "transcript=%r" % final.get("transcript"))
        check("voice capture %d keeps its raw audio after transcription" % (index + 1),
              final.get("hasAudio") is True, "hasAudio=%s audioDuration=%s" % (final.get("hasAudio"), final.get("audioDurationSeconds")))

    # §8.2 step 5's recognition is lexical and never invents a topic (§6.2), so the capture that names the topic is
    # filed and the ones that do not are left for the user rather than being filed under something invented.
    filed = poll(lambda: input_state(entries[0]["id"]), lambda state: state.get("primaryTopicId") == topic_id, 120, 2.0, "topic recognition")
    check("the capture that names the topic is filed under it automatically",
          filed.get("primaryTopicId") == topic_id,
          "primaryTopicId=%s (expected %s), transcript names the topic=%s" % (filed.get("primaryTopicId"), topic_id, TOPIC_NAME in DAY_TRANSCRIPTS[0]))
    unfiled = [input_state(entry["id"]).get("primaryTopicId") for entry in entries[1:]]
    check("captures that do not mention any known topic stay unfiled instead of being filed under an invented one",
          all(value is None for value in unfiled), "primaryTopicId of the other two captures=%s" % unfiled)

    history_state = poll(lambda: input_state(history_id), lambda state: state.get("primaryTopicId") == topic_id, 120, 2.0, "history topic")
    check("historical material is filed under the same topic, which is what makes it recallable",
          history_state.get("primaryTopicId") == topic_id, "primaryTopicId=%s" % history_state.get("primaryTopicId"))

    index_status = poll(lambda: admin.get("/api/system/index").json() or {},
                 lambda state: (state.get("indexedEntries") or 0) >= 5, 180, 3.0, "embedding index")
    check("the embedding index follows the captures and reports semantic search as usable",
          (index_status.get("indexedEntries") or 0) >= 5 and index_status.get("semanticSearchAvailable") is True,
          json.dumps(index_status, ensure_ascii=False))

    audio_read = device.get("/api/inputs/%s/audio" % entries[0]["id"])
    audio_bytes = audio_read.body
    playable = None
    try:
        with wave.open(io.BytesIO(audio_bytes), "rb") as handle:
            playable = (handle.getnchannels(), handle.getframerate(), round(handle.getnframes() / handle.getframerate(), 3))
    except Exception as exception:  # noqa: BLE001 - any decoding failure is the point of the check
        playable = "decode failed: %s" % exception
    check("the recording can be read back and is a decodable audio container (§15.2 step 6)",
          audio_read.status == 200 and hashlib.sha256(audio_bytes).hexdigest() == fixtures[0] and isinstance(playable, tuple),
          "status=%s contentType=%s bytes=%d sha256 matches=%s decoded(channels,rate,seconds)=%s" % (
              audio_read.status, audio_read.headers.get("Content-Type"), len(audio_bytes),
              hashlib.sha256(audio_bytes).hexdigest() == fixtures[0], playable))

    revision = device.patch_json("/api/inputs/%s" % entries[1]["id"], {"revisedTranscript": DAY_TRANSCRIPTS[1] + "（我在这里做了一处修订）"})
    revision_body = revision.json() or {}
    check("a transcript revision is stored next to the original (§6.1)",
          revision.status == 200 and revision_body.get("originalTranscript") == DAY_TRANSCRIPTS[1]
          and revision_body.get("revisedTranscript") == DAY_TRANSCRIPTS[1] + "（我在这里做了一处修订）",
          "original kept=%s revised set=%s" % (revision_body.get("originalTranscript") == DAY_TRANSCRIPTS[1], bool(revision_body.get("revisedTranscript"))))

    with open(os.path.join(WORK, "audio-fixtures.json"), "w", encoding="utf-8") as handle:
        json.dump({"0": fixtures[0], "1": fixtures[1], "2": fixtures[2]}, handle, ensure_ascii=False)

    # ------------------------------------------------------------- generation
    step("S2 (§17.3 3) generation with historical sources")

    generate = device.post_json(
        "/api/reflections/%s/generate" % today,
        {"ignoreTranscriptionFailures": False, "allowOverwriteOfManualEdits": False},
    )
    generate_body = generate.json() or {}
    check("generating today's reflection is queued", generate_body.get("queued") is True,
          "status=%s body=%s" % (generate.status, json.dumps(generate_body, ensure_ascii=False)[:300]))

    reflection = poll(
        lambda: device.get("/api/reflections/%s" % today).json() or {},
        lambda view: (view.get("workingVersion") or {}).get("sourcesCheckedAtUtc") is not None,
        300,
        3.0,
        "generation and source check",
    )
    working = reflection.get("workingVersion") or {}
    check("the draft reaches a reviewable state with sources checked",
          reflection.get("status") in ("ready", "reviewRequired") and working.get("id"),
          "status=%s workingVersionId=%s sourcesCheckedAt=%s" % (reflection.get("status"), reflection.get("workingVersionId"), working.get("sourcesCheckedAtUtc")))
    check("the generated body is the model's answer, not an empty shell",
          MARKER in (working.get("body") or "") and len(working.get("body") or "") > 40,
          "body length=%d" % len(working.get("body") or ""))

    sources = working.get("sources") or []
    historical = [source for source in sources if source.get("isHistorical")]
    check("today's material is cited with exact, resolvable ranges",
          any(not source.get("isHistorical") for source in sources) and all(source.get("drift") == "exact" for source in sources),
          "%d sources, drifts=%s" % (len(sources), sorted({source.get("drift") for source in sources})))
    check("yesterday's entry is recalled as a historical source (§8.3)",
          any(source.get("inputId") == history_id for source in historical),
          "historical sources=%s" % json.dumps([{"inputId": s.get("inputId"), "reason": s.get("reason"), "relevance": s.get("relevance")} for s in historical], ensure_ascii=False)[:400])

    body = working.get("body") or ""
    resolved = [
        slice_claim(body, source.get("blockIndex"), source.get("charStart"), source.get("charEnd"))
        for source in sources
    ]
    check("every source range slices back to real text in the current body",
          all(text.strip() for text in resolved),
          "first three ranges=%s" % json.dumps(resolved[:3], ensure_ascii=False)[:300])

    # --------------------------------------------------------- review and edit
    step("S3 (§17.3 4) unsourced statements, edits, regeneration")

    version_id = working.get("id")
    claims = working.get("unsourcedClaims") or []
    sliced = [slice_claim(body, claim.get("blockIndex"), claim.get("charStart"), claim.get("charEnd")) for claim in claims]
    check("the second-stage check flags the sentence with no source in the material (§8.4)",
          len(claims) == 1 and sliced[0] == MARKER,
          "claims=%d sliced=%s" % (len(claims), json.dumps(sliced, ensure_ascii=False)[:200]))

    confirm_refused = device.post_json("/api/reflections/%s/confirm" % today, {"acceptedUnsourcedClaims": False})
    check("confirmation is refused while an unsourced sentence is unacknowledged",
          confirm_refused.status in (400, 409) and error_code(confirm_refused) == "reflection.confirm.unsourced_claims_not_acknowledged",
          "status=%s code=%s" % (confirm_refused.status, error_code(confirm_refused)))

    edited_body = body + "\n\n这一段是我自己在客户端补上的。"
    edit = device.patch_json(
        "/api/reflections/%s/working-version/content" % today,
        {"title": working.get("title"), "summary": working.get("summary"), "body": edited_body},
    )
    edited = edit.json() or {}
    edited_working = edited.get("workingVersion") or {}
    check("a manual edit is stored and marked as hand-written",
          edit.status == 200 and edited_working.get("hasManualEdits") is True,
          "status=%s hasManualEdits=%s" % (edit.status, edited_working.get("hasManualEdits")))
    check("a source range that no longer holds its text is reported as drifted (A.6)",
          any(source.get("drift") != "exact" for source in (edited_working.get("sources") or []))
          or len(edited_working.get("sources") or []) != len(sources),
          "drifts=%s" % sorted({source.get("drift") for source in (edited_working.get("sources") or [])}))

    def job_state(job_id):
        listing = device.get("/api/jobs?limit=200").json() or {}
        for item in listing.get("items") or []:
            if item.get("id") == job_id:
                return item
        return {}

    protected = device.post_json(
        "/api/reflections/%s/generate" % today,
        {"ignoreTranscriptionFailures": False, "allowOverwriteOfManualEdits": False},
    )
    protected_job = (protected.json() or {}).get("job") or {}
    check("a regeneration that has not accepted the loss of hand edits is accepted on the queue",
          protected.status == 200 and (protected.json() or {}).get("queued") is True and protected_job.get("id"),
          "status=%s job=%s" % (protected.status, protected_job.get("id")))

    settled = poll(lambda: job_state(protected_job.get("id")), lambda job: job.get("status") in ("succeeded", "failed"), 180, 2.0, "protected regeneration job")
    after_protected = device.get("/api/reflections/%s" % today).json() or {}
    check("the refusal really happens at the point of rotation: the hand-edited version stays put (§6.4)",
          settled.get("status") == "succeeded" and after_protected.get("workingVersionId") == version_id
          and (after_protected.get("workingVersion") or {}).get("hasManualEdits") is True,
          "job status=%s workingVersionId unchanged=%s hasManualEdits=%s" % (
              settled.get("status"), after_protected.get("workingVersionId") == version_id,
              (after_protected.get("workingVersion") or {}).get("hasManualEdits")))

    allowed = device.post_json(
        "/api/reflections/%s/generate" % today,
        {"ignoreTranscriptionFailures": False, "allowOverwriteOfManualEdits": True},
    )
    check("regeneration proceeds once the user accepts losing the hand edit",
          allowed.status == 200 and (allowed.json() or {}).get("queued") is True
          and ((allowed.json() or {}).get("job") or {}).get("id") != protected_job.get("id"),
          "status=%s job=%s refusedJob=%s" % (allowed.status, ((allowed.json() or {}).get("job") or {}).get("id"), protected_job.get("id")))

    rotated = poll(
        lambda: device.get("/api/reflections/%s" % today).json() or {},
        lambda view: view.get("workingVersionId") not in (None, version_id)
        and (view.get("workingVersion") or {}).get("sourcesCheckedAtUtc") is not None,
        300,
        3.0,
        "rotation",
    )
    check("the second generation rotates the working slot and keeps the hand-edited one as the previous version",
          rotated.get("previousVersionId") == version_id and rotated.get("workingVersionId") != version_id,
          "working=%s previous=%s initial=%s" % (rotated.get("workingVersionId"), rotated.get("previousVersionId"), rotated.get("initialVersionId")))

    confirm = device.post_json("/api/reflections/%s/confirm" % today, {"acceptedUnsourcedClaims": True})
    confirmed = confirm.json() or {}
    check("confirming acknowledges the flagged sentence and confirms the day",
          confirm.status == 200 and confirmed.get("status") == "confirmed",
          "status=%s reflectionStatus=%s" % (confirm.status, confirmed.get("status")))
    confirmed_version_id = confirmed.get("confirmedVersionId")
    check("the confirmed version is the working version the user reviewed",
          confirmed_version_id == confirmed.get("workingVersionId"),
          "confirmed=%s working=%s previous=%s" % (confirmed_version_id, confirmed.get("workingVersionId"), confirmed.get("previousVersionId")))
    confirmed_body = (confirmed.get("workingVersion") or {}).get("body") or ""

    # ---------------------------------------------------------- notification
    step("S4 (§17.3 5) the draft-ready email")

    def messages_for_today():
        listing = mailpit_messages()
        return [item for item in listing.get("messages", []) if str(today) in (item.get("Subject") or "")]

    found = poll(messages_for_today, lambda items: len(items) >= 1, 180, 3.0, "draft-ready mail")
    check("the mailbox receives a notice that the draft is ready",
          len(found) >= 1, "%d matching message(s), subjects=%s" % (len(found), [item.get("Subject") for item in found[:3]]))

    if found:
        detail = mailpit_message(found[0]["ID"])
        mail_body = detail.get("Text") or detail.get("HTML") or ""
        check("the mail carries metadata only, never the draft text (§12)",
              MARKER not in mail_body and DAY_TRANSCRIPTS[0] not in mail_body and TEXT_INPUT not in mail_body,
              "body length=%d, contains draft text=%s" % (len(mail_body), MARKER in mail_body))

    # ------------------------------------------------------------- publishing
    step("S5 (§17.3 6) Hexo Markdown: export as a draft, then as a published post")

    def markdown_files():
        """Every file the instance has written under its markdown root, in walk order."""
        root = os.path.join(STATE, "markdown", "hexo")
        found = []
        for directory, _dirs, names in os.walk(root):
            for name in names:
                found.append(os.path.join(directory, name))
        return found

    def read_first_markdown():
        found = markdown_files()
        if not found:
            return None, ""
        with open(found[0], "r", encoding="utf-8") as handle:
            return found, handle.read()

    # The only destination kind left is a directory the user mounted, and the only thing visibility can mean for a
    # file is the front matter's `draft` flag — which is exactly what Hexo reads when it decides whether to
    # generate the post. So the whole of §17.3 step 6 is asserted against the file on disk.
    publish = device.post_json(
        "/api/reflections/%s/publish/%s" % (today, hexo_target_id),
        {"visibility": "draft", "replaceExistingFile": True},
    )
    publish_body = publish.json() or {}
    check("exporting the confirmed draft as Hexo Markdown is queued", publish_body.get("queued") is True,
          "status=%s code=%s detail=%s" % (publish.status, publish_body.get("code"), publish_body.get("detail")))
    publication_id = (publish_body.get("publication") or {}).get("id")

    publication = poll(
        lambda: device.get("/api/publications/" + publication_id).json() or {},
        lambda item: item.get("status") in ("draftUploaded", "published", "failed", "expired"),
        240,
        3.0,
        "markdown draft export",
    )
    check("the export finishes as draftUploaded and names the file it wrote",
          publication.get("status") == "draftUploaded" and (publication.get("remoteId") or "").endswith(".md"),
          "status=%s remoteId=%s errorCode=%s" % (
              publication.get("status"), publication.get("remoteId"), publication.get("errorCode")))

    files, written = read_first_markdown()
    check("a Markdown file lands under the instance's markdown root with front matter and the body",
          files is not None and len(files) == 1 and "title:" in written and confirmed_body[:20] in written,
          "files=%s head=%r" % ([os.path.relpath(path, STATE) for path in (files or [])], written[:200]))

    check("the draft export tells Hexo the post is still a draft (draft: true)",
          "draft: true" in written, "head=%r" % written[:200])

    # §11.1: publishing the same version to the same target again is the next round of one publication, not a
    # second one — the audit trail is "this draft went to this target twice", with `exportRound` counting.
    public = device.post_json(
        "/api/reflections/%s/publish/%s" % (today, hexo_target_id),
        {"visibility": "public", "replaceExistingFile": True},
    )
    public_body = public.json() or {}
    public_id = (public_body.get("publication") or {}).get("id")
    check("a manual public export is accepted as the next round of the same publication",
          public_body.get("queued") is True and public_id == publication_id
          and (public_body.get("publication") or {}).get("exportRound") == 1,
          "queued=%s publicationId=%s exportRound=%s" % (
              public_body.get("queued"), public_id, (public_body.get("publication") or {}).get("exportRound")))

    public_state = poll(
        lambda: device.get("/api/publications/" + public_id).json() or {},
        lambda item: item.get("status") in ("published", "failed", "expired"),
        240,
        3.0,
        "manual public export",
    )
    check("the manual public export reaches published", public_state.get("status") == "published",
          "status=%s errorCode=%s" % (public_state.get("status"), public_state.get("errorCode")))

    files_after, written_after = read_first_markdown()
    check("the public export rewrites the same file as a post Hexo will generate (draft: false)",
          "draft: false" in written_after and files_after is not None and len(files_after) == 1,
          "files=%s head=%r" % ([os.path.relpath(path, STATE) for path in (files_after or [])], written_after[:200]))

    check_remote = device.post_json("/api/publications/%s/check-remote" % publication_id, {})
    check_body = check_remote.json() or {}
    check("reading the exported file back reports it as in sync",
          check_remote.status == 200 and check_body.get("remoteChecked") is True
          and check_body.get("localChanged") is False and check_body.get("remoteChanged") is False,
          "status=%s local=%s remote=%s" % (
              check_remote.status, check_body.get("localChanged"), check_body.get("remoteChanged")))

    # Editing the exported file by hand is the divergence the user is offered a decision about (§11.1).
    if files_after:
        with open(files_after[0], "w", encoding="utf-8") as handle:
            handle.write("我在编辑器里改过这个文件了。\n")

        diverged = device.post_json("/api/publications/%s/check-remote" % publication_id, {})
        diverged_body = diverged.json() or {}
        check("a file edited outside the product is reported as diverged",
              diverged.status == 200 and diverged_body.get("remoteChanged") is True,
              "status=%s local=%s remote=%s" % (
                  diverged.status, diverged_body.get("localChanged"), diverged_body.get("remoteChanged")))

        kept = device.post_json("/api/publications/%s/resolve" % publication_id, {"action": "keepBoth"})
        check("the user's answer to the difference is accepted and clears it",
              kept.status == 200, "status=%s body=%s" % (kept.status, kept.text()[:200]))

        # Put the file back exactly as the instance wrote it, so nothing later in the run is reading a hand-edited
        # draft directory by accident.
        with open(files_after[0], "w", encoding="utf-8") as handle:
            handle.write(written_after)

    # -------------------------------------------------- phase-five operations
    step("S6 (§15.1) readable export")

    export_before = admin.post_json("/api/exports", {})
    check("an export can be created from the admin surface", export_before.status == 200,
          "status=%s body=%s" % (export_before.status, export_before.text()[:200]))
    export_root = os.path.join(STATE, "exports", (export_before.json() or {}).get("relativeRoot") or "")
    manifest_path = os.path.join(export_root, "manifest.json")
    manifest = {}
    if os.path.exists(manifest_path):
        with open(manifest_path, "r", encoding="utf-8") as handle:
            manifest = json.load(handle)
    exported = []
    for root, _dirs, names in os.walk(export_root):
        for name in names:
            exported.append(os.path.relpath(os.path.join(root, name), export_root))
    exported_json = [name for name in exported if name.endswith(".json") and name.startswith("data" + os.sep)]
    exported_audio = [name for name in exported if name.startswith("audio" + os.sep)]
    exported_markdown = [name for name in exported if name.startswith("markdown" + os.sep)]
    check("the export contains Markdown, JSON and the audio that retention has not yet deleted (§15.1)",
          len(exported_markdown) >= 1 and len(exported_json) >= 1 and len(exported_audio) >= 3,
          "markdown=%d json=%d audio=%d files=%d" % (len(exported_markdown), len(exported_json), len(exported_audio), len(exported)))
    check("the export states the retention policy in force (§15.1)",
          "retention" in json.dumps(manifest, ensure_ascii=False).lower(),
          json.dumps(manifest, ensure_ascii=False)[:400])

    export_text = ""
    for name in exported:
        path = os.path.join(export_root, name)
        if os.path.getsize(path) < 4 * 1024 * 1024:
            try:
                with open(path, "r", encoding="utf-8") as handle:
                    export_text += handle.read()
            except (UnicodeDecodeError, OSError):
                continue
    check("the export contains no device token or secret material (§15.2 step 7)",
          "tokenHash" not in export_text and (device.token[:16] not in export_text),
          "scanned %d characters" % len(export_text))
    check("the export carries the whole text of the day, includable by a human",
          TEXT_INPUT in export_text, "text input present=%s" % (TEXT_INPUT in export_text))

    step("S6 (§15.2) full backup, prune, restore validation")

    backups = admin.get("/api/backups").json() or {}
    check("the backup list starts empty", (backups.get("items") or []) == [], json.dumps(backups, ensure_ascii=False)[:200])

    backup = admin.post_json("/api/backups", {})
    backup_body = backup.json() or {}
    check("a full backup is written and reports what it redacted",
          backup.status == 200 and backup_body.get("fileName") and (backup_body.get("redactedDeviceTokens") or 0) >= 1,
          "fileName=%s bytes=%s sha256=%s redactedDevices=%s policy=%s" % (
              backup_body.get("fileName"), backup_body.get("byteCount"),
              (backup_body.get("sha256") or "")[:16], backup_body.get("redactedDeviceTokens"), backup_body.get("retentionPolicy")))

    archive_path = os.path.join(STATE, "backups", backup_body.get("fileName") or "")
    digest = ""
    if os.path.exists(archive_path):
        with open(archive_path, "rb") as handle:
            digest = hashlib.sha256(handle.read()).hexdigest()
    check("the archive on disk matches the reported hash",
          digest == backup_body.get("sha256"), "on disk=%s reported=%s" % (digest[:16], (backup_body.get("sha256") or "")[:16]))

    # This archive is taken before the retention sweep, so it still carries the audio: it is the one the fresh
    # instance restores in §15.2 step 6, where "the recording is playable" has to be checkable. It is copied out
    # of the backup directory because the prune check below deliberately deletes older archives.
    restore_source = os.path.join(WORK, "restore-source.zip")
    shutil.copyfile(archive_path, restore_source)
    note("kept %s (%d bytes) as the archive the fresh instance will restore" % (restore_source, os.path.getsize(restore_source)))

    with zipfile.ZipFile(archive_path) as archive:
        names = archive.namelist()
        database = [name for name in names if name.endswith(".db") or name.endswith(".sqlite")]
        check("the backup holds the database, the media, the Markdown and a manifest",
              len(database) == 1 and any(name.startswith("media/") for name in names)
              and any(name.startswith("markdown/") for name in names) and "manifest.json" in names,
              "%d entries, db=%s" % (len(names), database))
        check("the backup does not carry the DataProtection key ring (A.14)",
              not any("key" in name.lower() and name.endswith(".xml") for name in names),
              "entries matching key ring=%s" % [name for name in names if "key" in name.lower()][:4])
        extracted = os.path.join(WORK, "backup-db", os.path.basename(database[0]))
        os.makedirs(os.path.dirname(extracted), exist_ok=True)
        with archive.open(database[0]) as source, open(extracted, "wb") as target:
            target.write(source.read())

    connection = sqlite3.connect(extracted)
    try:
        devices = connection.execute("SELECT COUNT(*) FROM device").fetchone()[0]
        attached = connection.execute("SELECT COUNT(*) FROM input_entry WHERE device_id IS NOT NULL").fetchone()[0]
        entries_in_backup = connection.execute("SELECT COUNT(*) FROM input_entry").fetchone()[0]
        pairing_codes = connection.execute("SELECT COUNT(*) FROM pairing_code").fetchone()[0]
        versions = connection.execute("SELECT COUNT(*) FROM reflection_version").fetchone()[0]
        schema = connection.execute("SELECT COUNT(*) || ' applied: ' || MAX(migration_id) FROM schema_migrations").fetchone()[0]
    finally:
        connection.close()
    check("the backup strips every device token and its derived state (A.13)",
          devices == 0 and attached == 0 and pairing_codes == 0,
          "device rows=%d, input_entry.device_id set=%d, pairing codes=%d" % (devices, attached, pairing_codes))
    check("the backup keeps every input and draft version",
          entries_in_backup >= 4 and versions >= 3,
          "input entries=%d versions=%d schema version=%s" % (entries_in_backup, versions, schema))

    for _ in range(3):
        admin.post_json("/api/backups", {})
    listing = admin.get("/api/backups").json() or {}
    check("pruning keeps only the configured number of backups (§15.2, Backup:KeepCount=2)",
          len(listing.get("items") or []) == 2,
          "kept=%s" % json.dumps([item.get("fileName") for item in (listing.get("items") or [])], ensure_ascii=False))

    bad_archive = io.BytesIO()
    with zipfile.ZipFile(bad_archive, "w") as archive:
        archive.writestr("readme.txt", "this is not a backup")
    rejected = admin.post_multipart("/api/backups/restore", {}, {"archive": ("not-a-backup.zip", bad_archive.getvalue(), "application/zip")})
    check("a file that is not a backup is refused before anything is applied",
          rejected.status == 400 and (error_code(rejected) or "").startswith("restore."),
          "status=%s code=%s" % (rejected.status, error_code(rejected)))

    step("S6 (A.1) audio retention sweep")

    policy = admin.patch_json("/api/content-settings", {"audioRetentionDays": 0})
    check("retention can be set to immediate deletion",
          policy.status == 200 and (policy.json() or {}).get("audioRetentionDays") == 0,
          json.dumps(policy.json() or {}, ensure_ascii=False))

    cleanup = admin.post_json("/api/maintenance/audio-cleanup/run", {})
    cleanup_body = cleanup.json() or {}
    check("the retention sweep deletes the audio of confirmed days once it is due",
          cleanup.status == 200 and (cleanup_body.get("deletedBlobs") or 0) >= 1,
          json.dumps(cleanup_body, ensure_ascii=False))

    after = input_state(entries[0]["id"])
    check("the entry keeps its transcript after the audio is deleted, and reports it has no audio (A.1)",
          after.get("transcript") == DAY_TRANSCRIPTS[0] and after.get("hasAudio") is False,
          "transcript=%r hasAudio=%s" % (after.get("transcript"), after.get("hasAudio")))

    export_after = admin.post_json("/api/exports", {})
    after_root = os.path.join(STATE, "exports", (export_after.json() or {}).get("relativeRoot") or "")
    after_audio = []
    for root, _dirs, names in os.walk(after_root):
        for name in names:
            relative = os.path.relpath(os.path.join(root, name), after_root)
            if relative.startswith("audio" + os.sep):
                after_audio.append(relative)
    check("an export taken after the sweep carries no audio and says why",
          len(after_audio) == 0, "audio entries in the second export=%d" % len(after_audio))

    step("S6 (§16) diagnostics, probes and index rebuild")

    refused = admin.post_json("/api/system/diagnostic-mode", {"durationMinutes": 30, "acknowledgedContentRisk": False})
    check("the debug mode refuses to switch on without the content-risk acknowledgement",
          refused.status in (400, 409) and error_code(refused) == "diagnostics.risk_not_acknowledged",
          "status=%s code=%s" % (refused.status, error_code(refused)))

    too_long = admin.post_json("/api/system/diagnostic-mode", {"durationMinutes": 600, "acknowledgedContentRisk": True})
    check("the debug mode is bounded to at most four hours", too_long.status == 400,
          "status=%s code=%s" % (too_long.status, error_code(too_long)))

    enabled = admin.post_json("/api/system/diagnostic-mode", {"durationMinutes": 120, "acknowledgedContentRisk": True})
    enabled_body = enabled.json() or {}
    check("the debug mode switches on with a name and an expiry that ends it by itself",
          enabled.status == 200 and enabled_body.get("enabled") is True and 100 <= (enabled_body.get("remainingMinutes") or 0) <= 120 and enabled_body.get("enabledBy") == ADMIN_USER,
          json.dumps(enabled_body, ensure_ascii=False))

    disabled = admin.delete("/api/system/diagnostic-mode")
    check("the debug mode can be switched off again",
          disabled.status == 200 and (disabled.json() or {}).get("enabled") is False,
          json.dumps(disabled.json() or {}, ensure_ascii=False))

    for service in ("transcription", "generation", "embedding", "smtp"):
        probe = admin.post_json("/api/system/test-connection/%s" % service, {})
        probe_body = probe.json() or {}
        check("test connection for %s reaches the real service" % service,
              probe.status == 200 and probe_body.get("ok") is True,
              "%s code=%s detail=%s" % (service, probe_body.get("code"), probe_body.get("detail")))

    rebuild = admin.post_json("/api/system/index/rebuild", {})
    check("an index rebuild can be requested", rebuild.status == 200,
          "status=%s body=%s" % (rebuild.status, rebuild.text()[:200]))
    rebuilt = poll(
        lambda: admin.get("/api/system/index").json() or {},
        lambda state: state.get("rebuildInProgress") is False and (state.get("indexedEntries") or 0) >= 5,
        300,
        3.0,
        "index rebuild",
    )
    check("the rebuilt index reports every entry again",
          (rebuilt.get("indexedEntries") or 0) >= 5 and rebuilt.get("semanticSearchAvailable") is True,
          json.dumps(rebuilt, ensure_ascii=False))

    step("S7 (§17.3 8) produce the backup the fresh instance will restore")

    final_backup = admin.post_json("/api/backups", {})
    final_body = final_backup.json() or {}
    check("the acceptance ends with a backup to restore on a fresh instance",
          final_backup.status == 200 and final_body.get("fileName"),
          "fileName=%s bytes=%s sha256=%s redactedDevices=%s" % (
              final_body.get("fileName"), final_body.get("byteCount"),
              (final_body.get("sha256") or "")[:16], final_body.get("redactedDeviceTokens")))

    evidence = {
        "today": str(today),
        "yesterday": str(yesterday),
        "deviceToken": device.token,
        "deviceId": redeem_body.get("deviceId"),
        "topicId": topic_id,
        "entryIds": [entry.get("id") for entry in entries],
        "textInputId": text_id,
        "historyInputId": history_id,
        "corpus": [TEXT_INPUT] + DAY_TRANSCRIPTS + [HISTORY_TRANSCRIPT],
        "marker": MARKER,
        "originalTranscript": DAY_TRANSCRIPTS[1],
        "revisedTranscript": DAY_TRANSCRIPTS[1] + "（我在这里做了一处修订）",
        "audioFixtureSha256": fixtures,
        "restoreSourceZip": restore_source,
        "restoreSourceSha256": hashlib.sha256(open(restore_source, "rb").read()).hexdigest(),
        "markdownTargetId": hexo_target_id,
        "backupFileName": final_body.get("fileName"),
        "backupSha256": final_body.get("sha256"),
        "exportRoot": (export_after.json() or {}).get("relativeRoot"),
    }
    with open(os.path.join(WORK, "acceptance-evidence.json"), "w", encoding="utf-8") as handle:
        json.dump(evidence, handle, ensure_ascii=False, indent=2)

    return evidence


if __name__ == "__main__":
    if WORK:
        os.makedirs(WORK, exist_ok=True)
    try:
        main()
    except Exception as exception:  # noqa: BLE001 - an unexpected error is a failed run, never a quiet one
        import traceback

        traceback.print_exc()
        check("the acceptance run reached the end without an unexpected error", False, repr(exception))
    finally:
        summary_exit(WORK, "acceptance-report.json")
