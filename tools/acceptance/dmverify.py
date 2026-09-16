#!/usr/bin/env python3
"""Shared helpers for the phase-5 acceptance and restore verification scripts."""

import hashlib
import http.cookiejar
import io
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

results = []
current_step = "setup"


def step(name):
    global current_step
    current_step = name
    print("\n=== %s ===" % name, flush=True)


def check(label, ok, evidence=""):
    results.append({"step": current_step, "label": label, "ok": bool(ok), "evidence": str(evidence)})
    print("[%s] %s :: %s" % ("PASS" if ok else "FAIL", label, evidence), flush=True)
    return bool(ok)


def note(text):
    print("       %s" % text, flush=True)


def summary_exit(work, report_name):
    failures = [item for item in results if not item["ok"]]
    print("\n=== summary ===")
    print("checks: %d, failed: %d" % (len(results), len(failures)))
    for item in failures:
        print("  FAIL [%s] %s :: %s" % (item["step"], item["label"], item["evidence"][:300]))
    if work:
        with open(os.path.join(work, report_name), "w", encoding="utf-8") as handle:
            json.dump(results, handle, ensure_ascii=False, indent=2)
    sys.exit(1 if failures else 0)


class Response:
    def __init__(self, status, body, headers):
        self.status = status
        self.body = body
        self.headers = headers

    def json(self):
        if not self.body:
            return None
        try:
            return json.loads(self.body.decode("utf-8"))
        except Exception:
            return None

    def text(self):
        return self.body.decode("utf-8", "replace")


class Api:
    """One HTTP client with its own cookie jar. Admin and device calls must not share one: a device
    request that inherited the admin cookie would be authenticated as the administrator, which is
    exactly the mistake that hid an authorisation bug during phase four."""

    def __init__(self, base, token=None):
        self.base = base.rstrip("/")
        self.token = token
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))

    def _send(self, method, path, data=None, headers=None, timeout=120):
        url = self.base + path
        head = dict(headers or {})
        if self.token:
            head["Authorization"] = "Bearer " + self.token
        request = urllib.request.Request(url, data=data, method=method, headers=head)
        try:
            with self.opener.open(request, timeout=timeout) as response:
                return Response(response.status, response.read(), dict(response.headers))
        except urllib.error.HTTPError as error:
            return Response(error.code, error.read(), dict(error.headers))

    def get(self, path, timeout=180):
        return self._send("GET", path, timeout=timeout)

    def post_json(self, path, payload, timeout=180):
        return self._send("POST", path, json.dumps(payload).encode("utf-8"), {"Content-Type": "application/json"}, timeout)

    def patch_json(self, path, payload, timeout=180):
        return self._send("PATCH", path, json.dumps(payload).encode("utf-8"), {"Content-Type": "application/json"}, timeout)

    def delete(self, path, timeout=180):
        return self._send("DELETE", path, timeout=timeout)

    def post_form(self, path, fields, timeout=180):
        body = urllib.parse.urlencode(fields).encode("utf-8")
        return self._send("POST", path, body, {"Content-Type": "application/x-www-form-urlencoded"}, timeout)

    def post_multipart(self, path, fields, files, timeout=600):
        boundary = "----dailymusings" + hashlib.sha1(os.urandom(16)).hexdigest()
        buffer = io.BytesIO()
        for name, value in fields.items():
            buffer.write(("--%s\r\n" % boundary).encode("utf-8"))
            buffer.write(('Content-Disposition: form-data; name="%s"\r\n\r\n' % name).encode("utf-8"))
            buffer.write(str(value).encode("utf-8"))
            buffer.write(b"\r\n")
        for name, (filename, content, content_type) in files.items():
            buffer.write(("--%s\r\n" % boundary).encode("utf-8"))
            buffer.write(('Content-Disposition: form-data; name="%s"; filename="%s"\r\n' % (name, filename)).encode("utf-8"))
            buffer.write(("Content-Type: %s\r\n\r\n" % content_type).encode("utf-8"))
            buffer.write(content)
            buffer.write(b"\r\n")
        buffer.write(("--%s--\r\n" % boundary).encode("utf-8"))
        return self._send("POST", path, buffer.getvalue(), {"Content-Type": "multipart/form-data; boundary=" + boundary}, timeout)


def error_code(response):
    payload = response.json()
    return payload.get("code") if isinstance(payload, dict) else None


def poll(fn, predicate, timeout_seconds, interval=2.0, label=""):
    deadline = time.time() + timeout_seconds
    last = None
    while time.time() < deadline:
        last = fn()
        if predicate(last):
            return last
        time.sleep(interval)
    note("poll timed out for %s; last value: %s" % (label, json.dumps(last, ensure_ascii=False, default=str)[:600]))
    return last
