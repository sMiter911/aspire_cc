#!/usr/bin/env python3
"""End-to-end demonstration of the asynchronous todo workflow (stdlib only).

    python scripts/demo.py --url http://localhost:<frontend-port> --count 20 --admin-password-env DEMO_ADMIN_PASSWORD

What it does (the Phase 3 demonstration scenario):
  1. registers a throw-away USER and signs in through Spring Boot,
  2. creates --count todos through the ASP.NET Core Todo API (each is QUEUED immediately),
  3. polls and prints every status transition with timestamps, so you can see the Go worker processing
     WORKER_CONCURRENCY jobs at a time,
  4. when everything is WAITING_RELEASE: signs in as the ADMIN (password from an environment variable, never
     from the command line) and either releases manually (--release) or waits for the worker's timer,
  5. prints the final timeline and a concurrency summary.
"""
import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
import uuid

parser = argparse.ArgumentParser()
parser.add_argument("--url", required=True, help="frontend origin (it proxies both APIs), e.g. http://localhost:5173")
parser.add_argument("--count", type=int, default=20)
parser.add_argument("--release", action="store_true", help="release manually as ADMIN instead of waiting for the timer")
parser.add_argument("--admin-email", default="admin@example.com")
parser.add_argument("--admin-password-env", default="DEMO_ADMIN_PASSWORD", help="name of the env var holding the admin password")
parser.add_argument("--timeout", type=int, default=420)
args = parser.parse_args()


def call(method, path, token=None, body=None):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(args.url + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req) as r:
            text = r.read().decode()
            return r.status, (json.loads(text) if text else None)
    except urllib.error.HTTPError as e:
        text = e.read().decode()
        try:
            return e.code, json.loads(text)
        except ValueError:
            return e.code, text


def must(result, expected, what):
    status, body = result
    if status != expected:
        sys.exit(f"{what} failed: HTTP {status} {body}")
    return body


stamp = time.strftime("%H:%M:%S")
email = f"demo-{uuid.uuid4().hex[:8]}@example.com"
password = "Correct-Horse-Battery-9"
must(call("POST", "/api/auth/register", body={"email": email, "password": password, "firstName": "Demo", "lastName": "User"}), 201, "register")
user_token = must(call("POST", "/api/auth/login", body={"email": email, "password": password}), 200, "login")["accessToken"]
print(f"[{stamp}] signed in as {email}")

ids = {}
t0 = time.time()
for i in range(1, args.count + 1):
    todo = must(call("POST", "/api/todos", user_token, {"title": f"Learn Go concurrency #{i}"}), 201, "create todo")
    ids[todo["id"]] = {"n": i, "history": [(0.0, todo["processingStatus"])]}
print(f"created {args.count} todos in {time.time() - t0:.1f}s; every one starts as QUEUED")


def poll():
    status, page = call("GET", "/api/todos?size=100", user_token)
    if status == 401:  # access token (10 min) expired during a long demo
        return None
    for t in page["content"]:
        rec = ids.get(t["id"])
        if rec and rec["history"][-1][1] != t["processingStatus"]:
            rec["history"].append((time.time() - t0, t["processingStatus"]))
    return {i: r["history"][-1][1] for i, r in ids.items()}


def wait_for(target, label):
    deadline = time.time() + args.timeout
    last = ""
    while time.time() < deadline:
        current = poll()
        if current is None:
            sys.exit("access token expired; rerun")
        summary = {}
        for s in current.values():
            summary[s] = summary.get(s, 0) + 1
        line = ", ".join(f"{k}={v}" for k, v in sorted(summary.items()))
        if line != last:
            print(f"  +{time.time() - t0:5.1f}s  {line}")
            last = line
        if all(s == target for s in current.values()):
            return
        if any(s == "FAILED" for s in current.values()):
            sys.exit("a todo FAILED (see the worker logs and the admin page)")
        time.sleep(0.5)
    sys.exit(f"timed out waiting for {label}")


print("waiting for the worker to process everything (QUEUED -> PROCESSING -> WAITING_RELEASE)...")
wait_for("WAITING_RELEASE", "processing")

if args.release:
    password_admin = os.environ.get(args.admin_password_env)
    if not password_admin:
        sys.exit(f"set {args.admin_password_env} to the admin password to use --release")
    admin_token = must(call("POST", "/api/auth/login", body={"email": args.admin_email, "password": password_admin}), 200, "admin login")["accessToken"]
    status, _ = call("POST", f"/api/admin/todos/{next(iter(ids))}/release", user_token)
    print(f"a USER trying to release gets HTTP {status} (expected 403)")
    body = must(call("POST", "/api/admin/todos/release", admin_token), 202, "release all")
    print(f"ADMIN requested release of everything waiting ({body['waiting']} known to the API)")
else:
    print("waiting for the worker's release timer (RELEASE_INTERVAL)...")

wait_for("COMPLETED", "release")

print("\nper-todo timeline (seconds since the first create):")
starts = []
for rec in ids.values():
    steps = " -> ".join(f"{s}@{t:.1f}" for t, s in rec["history"])
    starts.append(next((t for t, s in rec["history"] if s == "PROCESSING"), None))
    print(f"  #{rec['n']:>2}: {steps}")
proc = sorted(t for t in starts if t is not None)
if proc:
    batches = sum(1 for i, t in enumerate(proc) if i == 0 or t - proc[i - 1] > 1.0)
    print(f"\nPROCESSING started in {batches} burst(s) over {proc[-1] - proc[0]:.1f}s "
          f"(bounded concurrency shows as bursts of at most WORKER_CONCURRENCY jobs)")
print("done")
