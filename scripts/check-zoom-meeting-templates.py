#!/usr/bin/env python3
"""
One-off diagnostic: calls Zoom's GET /users/{userId}/meeting_templates for a given Team and
prints the raw response, so we can see whether the account actually has any usable templates
before building a UI around them.

Why this exists: Zoom's "create meeting from template_id" only works with Admin-type templates,
not personal ones -- even though the list endpoint returns both. This script's only job is to
answer "does this account have any Admin-type templates at all" before writing real feature code
against an assumption that might not hold on this Zoom plan. See the Zoom devforum threads
referenced in the "Zoom template" TODO discussion for the underlying constraint.

Never prints ZoomClientSecret or the OAuth access token -- only the meeting_templates response,
which contains no credentials.

Usage:
    python3 scripts/check-zoom-meeting-templates.py db [path-to-db] [team-id]
    python3 scripts/check-zoom-meeting-templates.py user-secrets [project-path]

    "db" mode reads Team.Zoom* columns (defaults: vesessionmanager.db, team-id 1) -- for a real
    deployed database where the multi-team Team row has been filled in.

    "user-secrets" mode shells out to `dotnet user-secrets list --project <project-path>` (default
    src/VeSessionManager.Worker) and parses the Zoom:AccountId/ClientId/ClientSecret lines --
    for the pre-multi-team credentials some projects still have sitting in user-secrets. Values
    never pass through this script's own arguments or stdout either way, only through dotnet's own
    subprocess pipe.
"""

import base64
import json
import re
import sqlite3
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request

TOKEN_URL = "https://zoom.us/oauth/token"
API_BASE = "https://api.zoom.us/v2"


def load_credentials_from_db(db_path, team_id):
    conn = sqlite3.connect(db_path)
    cur = conn.cursor()
    cur.execute(
        "SELECT Name, ZoomAccountId, ZoomClientId, ZoomClientSecret, ZoomUserId FROM Teams WHERE Id = ?",
        (team_id,),
    )
    row = cur.fetchone()
    conn.close()
    if row is None:
        sys.exit(f"No Team row with Id={team_id} found in {db_path}")

    name, account_id, client_id, client_secret, zoom_user_id = row
    missing = [
        label
        for label, value in [("ZoomAccountId", account_id), ("ZoomClientId", client_id), ("ZoomClientSecret", client_secret)]
        if not value
    ]
    if missing:
        sys.exit(f"Team '{name}' (Id={team_id}) is missing: {', '.join(missing)} -- set these via direct DB edit first.")

    return name, account_id, client_id, client_secret, (zoom_user_id or "me")


def load_credentials_from_user_secrets(project_path):
    result = subprocess.run(
        ["dotnet", "user-secrets", "list", "--project", project_path],
        capture_output=True, text=True, check=True,
    )
    values = {}
    for line in result.stdout.splitlines():
        m = re.match(r"^Zoom:(AccountId|ClientId|ClientSecret)\s*=\s*(.+)$", line.strip())
        if m:
            values[m.group(1)] = m.group(2)

    missing = [k for k in ("AccountId", "ClientId", "ClientSecret") if k not in values]
    if missing:
        sys.exit(f"user-secrets for {project_path} is missing Zoom:{', Zoom:'.join(missing)}")

    return "(user-secrets)", values["AccountId"], values["ClientId"], values["ClientSecret"], "me"


def get_access_token(account_id, client_id, client_secret):
    basic_auth = base64.b64encode(f"{client_id}:{client_secret}".encode()).decode()
    body = f"grant_type=account_credentials&account_id={account_id}".encode()
    request = urllib.request.Request(
        TOKEN_URL,
        data=body,
        method="POST",
        headers={
            "Authorization": f"Basic {basic_auth}",
            "Content-Type": "application/x-www-form-urlencoded",
        },
    )
    with urllib.request.urlopen(request) as response:
        return json.load(response)["access_token"]


def list_meeting_templates(access_token, zoom_user_id):
    request = urllib.request.Request(
        f"{API_BASE}/users/{urllib.parse.quote(zoom_user_id, safe='')}/meeting_templates",
        headers={"Authorization": f"Bearer {access_token}"},
    )
    with urllib.request.urlopen(request) as response:
        return json.load(response)


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "db"

    if mode == "user-secrets":
        project_path = sys.argv[2] if len(sys.argv) > 2 else "src/VeSessionManager.Worker"
        team_name, account_id, client_id, client_secret, zoom_user_id = load_credentials_from_user_secrets(project_path)
    else:
        db_path = sys.argv[2] if len(sys.argv) > 2 else "vesessionmanager.db"
        team_id = int(sys.argv[3]) if len(sys.argv) > 3 else 1
        team_name, account_id, client_id, client_secret, zoom_user_id = load_credentials_from_db(db_path, team_id)
    print(f"Checking meeting templates for team '{team_name}' (Zoom user: {zoom_user_id})...\n")

    try:
        token = get_access_token(account_id, client_id, client_secret)
    except urllib.error.HTTPError as e:
        sys.exit(f"OAuth token request failed ({e.code}): {e.read().decode()}")

    try:
        result = list_meeting_templates(token, zoom_user_id)
    except urllib.error.HTTPError as e:
        sys.exit(f"meeting_templates request failed ({e.code}): {e.read().decode()}")

    templates = result.get("templates", [])
    if not templates:
        print("No templates returned at all -- raw response:")
        print(json.dumps(result, indent=2))
    else:
        print(f"{len(templates)} template(s) found:\n")
        for t in templates:
            print(json.dumps(t, indent=2))
