# Runbook — Stand up a new server

**When:** a fresh box (new deployment, rebuild, or someone self-hosting this app).
**Why it works this way:** [`docs/deployment.md`](../docs/deployment.md),
[`docs/configuration.md`](../docs/configuration.md).

---

## 1. Bootstrap the box

`ops/setup-server.sh` (gitignored — the copy in this working tree is the source, hand-copied to the
box) creates:

- the `vesessionmanager` **system account** — no shell, no home, nothing logs in as it,
- `/opt/vesessionmanager/{worker,web}/` — the app path, `rsync --delete`d on every deploy,
- `/var/lib/vesessionmanager/` — the **database**, deliberately outside the app path,
- `/var/lib/vesessionmanager-keys/` at **0700** — the key ring, a *sibling* not a child,
- `/etc/sudoers.d/vesessionmanager-deploy` — **one exact rule per unit**, no wildcards,
- both systemd units, **enabled but not started** (nothing is published yet).

Two things that will silently bite:

- `chmod 0440` on the sudoers file and `visudo -c` before installing it. `tee`/`cat` create it with
  your umask, and sudo **silently ignores** a file with any other mode — every sudo call then
  demands a password, which reads like a broken SSH key.
- The units set `WorkingDirectory`, because the content root is the current directory. Any by-hand
  invocation needs `sh -c 'cd /opt/vesessionmanager/worker && exec dotnet ./VeSessionManager.Worker.dll <switch>'`.

## 2. Create the archive directory (ARRL filing)

```bash
sudo mkdir -p /var/lib/vesessionmanager/vec-archives
sudo chown vesessionmanager:vesessionmanager /var/lib/vesessionmanager/vec-archives
```

Outside the app path for the same reason the database is — `deploy.yml` runs `rsync --delete` over
the app path on every release.

⚠️ **Add it to this box's off-box backup**, alongside the database and key ring. An unbacked-up
archive fails silently: nothing looks wrong until a receipt is wanted and missing.

## 3. Set the hostname-dependent config

`AllowedHosts` is **pinned** in Web's `appsettings.Production.json`. A deployment served under any
other hostname — staging name, bare IP — returns **400 Bad Request for every request**. Update it
and `App:PublicBaseUrl` beside it (both take semicolon-separated lists) if this box is not the
pinned host.

## 4. CI secrets and the deploy key

Still manual after the bootstrap script:

1. Install the CI deploy key into the deploy user's `authorized_keys`.
2. Add the repo's Actions secrets: Tailscale client id/secret, SSH key, deploy host, deploy user.
3. Apache vhost + TLS certificate once a domain exists.
4. Point the **off-box backup** at this box (BackupScripts) — database and key ring to separate
   destinations under separate keys.

## 5. First deploy

Push a version tag — see [`deploy-a-release.md`](deploy-a-release.md).

Expect the **Web unit to report failed** on this first run if step 6 is skipped. That is the
safeguard working, not a broken build.

## 6. Create the first administrator — before starting Web

A Production database starts with **no account anyone can sign into**. `DevAuthSeeder` runs only in
Development, and every route that could create a user is itself `[Authorize]`d.

```bash
dotnet /opt/vesessionmanager/web/VeSessionManager.Web.dll --create-admin \
  --email you@example.org --name "Your Name" [--callsign WX0MIK]
```

- Applies migrations itself, so it is safe before either service has ever started.
- Prints a generated password **once** to stdout, then exits without starting the host.
- Refuses if that email already exists.
- To supply your own password, set `VSM_ADMIN_PASSWORD` in the environment. **Never pass it as an
  argument** — arguments are visible in shell history and to anyone who can run `ps`.

Then:

```bash
sudo systemctl start vesessionmanager-web
```

If Web was started first it **restart-loops every ten seconds** logging
`Refusing to start: no account on this deployment can sign in`. It self-heals the moment an
administrator exists. The Worker is unaffected and will happily poll ExamTools with no accounts at
all — only Web has a login surface to protect.

## 7. Per-team configuration

Nothing is defaulted. For each team, in Admin → Team Settings:

- **ExamTools** username/password — the one hard requirement; everything else depends on ingestion.
  Note every ExamTools action is attributed to this account in *their* audit log, not to the person
  who clicked here.
- **Zoom, Discord, Square, SMTP** — all optional. Each skips quietly with one INFO line while
  unconfigured and starts working on the very next poll once set; there is no backfill step.
- **Square**: access token, location ID, webhook URL and signature key must all come from the
  **same** environment, and `Team.SquareEnvironment` must agree. New teams default to **Sandbox** —
  set a live team back to Production deliberately. See
  [`square-payment-not-recorded.md`](square-payment-not-recorded.md).
- **ARRL settings**, if the team files with ARRL-VEC.

## 8. Before it is allowed to touch the real world

A configured team's Worker will poll ExamTools, create Zoom/Discord events, issue Square links and
**mail real candidates** on its next tick. If this box is not meant to do that yet, turn on email
test mode (Admin → System Settings) — it redirects **every** send deployment-wide and refuses to
turn on without an override address.
