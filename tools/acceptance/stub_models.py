#!/usr/bin/env python3
"""OpenAI-compatible model stub used only for the phase-5 end-to-end acceptance run.

It is deliberately predictable so that the acceptance script can assert on what came back:

* transcription  - the audio's sha256 is looked up in STUB_TRANSCRIPT_MAP (a JSON file the
                   driver writes) so the script knows the exact transcript it should see.
* generation     - the draft body is built from the material lines of the prompt, so every
                   paragraph is quotable verbatim and the citation map really resolves. One
                   extra sentence is appended on purpose: the unsourced-statement check must
                   find something, and that is a path §17.3 step 4 requires.
* check          - reports exactly that appended sentence.
* embeddings     - deterministic vectors from a hash of the input, so retrieval is stable.

Only the acceptance run uses this. It is never part of the product.
"""

import hashlib
import json
import os
import re
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MARKER = "下午我顺路去看了那家旧书店。"
DIMENSIONS = int(os.environ.get("STUB_DIMENSIONS", "64"))
MAP_PATH = os.environ.get("STUB_TRANSCRIPT_MAP", "")
STATS_PATH = os.environ.get("STUB_STATS", "")

MATERIAL_RE = re.compile(r"^\[(S\d+)\]\s+(?:\((\d{4}-\d{2}-\d{2})\)\s+)?(.*)$")

_transcripts = {}
_stats = {"requests": 0, "transcriptions": 0, "generations": 0, "checks": 0, "embeddings": 0}
_lock = threading.Lock()


def load_map():
    global _transcripts
    if MAP_PATH and os.path.exists(MAP_PATH):
        with open(MAP_PATH, "r", encoding="utf-8") as handle:
            _transcripts = json.load(handle)


def bump(key):
    with _lock:
        _stats["requests"] += 1
        _stats[key] = _stats.get(key, 0) + 1
        if STATS_PATH:
            with open(STATS_PATH, "w", encoding="utf-8") as handle:
                json.dump(_stats, handle, ensure_ascii=False)


def vector_for(text):
    digest = hashlib.sha256(text.encode("utf-8")).digest()
    values = []
    while len(values) < DIMENSIONS:
        for byte in digest:
            values.append((byte - 127.5) / 127.5)
            if len(values) == DIMENSIONS:
                break
        digest = hashlib.sha256(digest).digest()
    return values


def parse_materials(prompt):
    """Returns (day_materials, historical_materials) as lists of (label, date, text)."""
    day = []
    historical = []
    current = day
    for line in prompt.splitlines():
        stripped = line.strip()
        if stripped.startswith("可以引用的历史素材"):
            current = historical
            continue
        if stripped.startswith("没有可引用的历史素材"):
            current = []
            continue
        match = MATERIAL_RE.match(stripped)
        if match:
            current.append((match.group(1), match.group(2), match.group(3).strip()))
    return day, historical


def build_generation(prompt):
    day, historical = parse_materials(prompt)
    date_match = re.search(r"内容日期：(\d{4}-\d{2}-\d{2})", prompt)
    content_date = date_match.group(1) if date_match else time.strftime("%Y-%m-%d")

    paragraphs = []
    citations = []

    for label, _date, text in day:
        paragraphs.append(text)
        citations.append(
            {
                "quote": text,
                "sources": [label],
                "relevance": 0.9,
                "reason": "这段文字逐字来自当天的素材。",
            }
        )

    for label, date, text in historical:
        paragraph = "之前也记下过这样的事。" + text
        paragraphs.append(paragraph)
        citations.append(
            {
                "quote": text,
                "sources": [label],
                "relevance": 0.8,
                "reason": "这段文字来自 " + (date or "更早") + " 的素材，写成延续。",
            }
        )

    paragraphs.append(MARKER)

    first = day[0][2] if day else (historical[0][2] if historical else "")
    body = "\n\n".join(paragraphs)

    return {
        "title": "随想（" + content_date + "）",
        "summary": first[:40],
        "body": body,
        "tags": ["随想", "记录"],
        "categories": ["日记"],
        "citations": citations,
    }


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "dailymusings-stub/1.0"

    def log_message(self, fmt, *args):  # keep stderr readable but still informative
        sys.stderr.write("stub %s\n" % (fmt % args))

    def _send(self, payload, status=200):
        raw = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def _read_body(self):
        length = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(length) if length else b""

    def do_GET(self):
        if self.path.endswith("/models"):
            bump("models")
            self._send({"object": "list", "data": [{"id": "stub-chat", "object": "model"}]})
            return
        if self.path.endswith("/_stats"):
            self._send(_stats)
            return
        bump("unknown")
        self._send({"error": {"message": "not found"}}, status=404)

    def do_POST(self):
        body = self._read_body()

        if self.path.endswith("/audio/transcriptions"):
            load_map()
            # The upload is multipart, so the audio's own bytes are one of the parts; the driver hashed
            # exactly those bytes when it built the fixture.
            text = _transcripts.get(_find_digest(body), "（未登记的音频）")
            bump("transcriptions")
            self._send({"text": text, "language": "zh"})
            return

        try:
            request = json.loads(body.decode("utf-8"))
        except Exception:
            self._send({"error": {"message": "bad request"}}, status=400)
            return

        if self.path.endswith("/chat/completions"):
            messages = request.get("messages") or []
            prompt = messages[-1].get("content", "") if messages else ""
            content = json.dumps(self._chat(prompt), ensure_ascii=False)
            self._send(
                {
                    "id": "stub-1",
                    "object": "chat.completion",
                    "model": request.get("model", "stub"),
                    "choices": [
                        {"index": 0, "message": {"role": "assistant", "content": content}, "finish_reason": "stop"}
                    ],
                }
            )
            return

        if self.path.endswith("/embeddings"):
            inputs = request.get("input") or []
            if isinstance(inputs, str):
                inputs = [inputs]
            bump("embeddings")
            self._send(
                {
                    "object": "list",
                    "data": [
                        {"object": "embedding", "index": index, "embedding": vector_for(item)}
                        for index, item in enumerate(inputs)
                    ],
                }
            )
            return

        bump("unknown")
        self._send({"error": {"message": "not found"}}, status=404)

    def _chat(self, prompt):
        if "下面是一篇已经写好的正文" in prompt:
            bump("checks")
            if MARKER in prompt:
                return {"unsourced": [{"quote": MARKER, "reason": "素材里没有提到旧书店。"}]}
            return {"unsourced": []}

        bump("generations")
        return build_generation(prompt)


def _find_digest(body):
    """The upload is multipart; the audio bytes are what the driver hashed, so hash every run of
    bytes between boundaries and look each up. Only a handful of parts exist, so this is cheap."""
    for part in body.split(b"\r\n--"):
        if b"\r\n\r\n" not in part:
            continue
        payload = part.split(b"\r\n\r\n", 1)[1]
        digest = hashlib.sha256(payload).hexdigest()
        if digest in _transcripts:
            return digest
    return ""


if __name__ == "__main__":
    port = int(os.environ.get("STUB_PORT", "8077"))
    load_map()
    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    sys.stderr.write("stub listening on %d, %d transcripts loaded\n" % (port, len(_transcripts)))
    sys.stderr.flush()
    server.serve_forever()
