# Deployment

## Overview

The app is deployed to Ubuntu Linux as **two** systemd services sharing one SQLite database,
behind an Apache reverse proxy with SSL via Let's Encrypt (Web only — the Worker has no public
endpoint).

| Item | Detail |
|---|---|
| Server | Same Ubuntu box as `NcsScheduler`, reachable only over Tailscale |
| App path | `/opt/vesessionmanager/{worker,web}/` |
| Database path | `/var/lib/vesessionmanager/vesessionmanager.db` — **deliberately outside** the app path (see below) |
| Data Protection key ring | `/var/lib/vesessionmanager/dataprotection-keys/` — same directory, same reasoning; see below |
| Service account | `vesessionmanager` (dedicated, distinct from NcsScheduler's `www-data` on the same box) |
| Worker service | `vesessionmanager-worker.service` — background jobs, no listening port |
| Web service | `vesessionmanager-web.service` — `ASPNETCORE_URLS=http://localhost:5100` |
| Reverse proxy | Apache with `ProxyPreserveHost On`, Web only |
| SSL | Let's Encrypt |
| Public domain(s) | `ve.wx0mik.radio` (decided 2026-07-22) — a second domain may be added later for a second team; see "Apache Virtual Host" below |

**Why the DB lives outside the app path:** unlike NcsScheduler (whose SQLite file sits inside
`/opt/ncsscheduler/`, the same tree its deploy `rsync --delete`s, protected only by an `--exclude`
flag on every run), VeSessionManager's `appsettings.Production.json` already points the connection
string at `/var/lib/vesessionmanager/vesessionmanager.db` — physically outside
`/opt/vesessionmanager/{worker,web}/` entirely. An `rsync --delete` against the app folders can
never touch it, exclude flags or not. `/var/lib/` is also the conventionally-correct FHS location
for a service's variable data, vs. `/opt/` for its binaries.

**Data Protection key ring (2026-07-30, see `docs/credential-encryption.md`):** `Team`'s per-team
credential columns (ExamTools/Zoom/Square/SMTP secrets) are encrypted at rest via ASP.NET Core's
Data Protection API. Both `vesessionmanager-worker` and `vesessionmanager-web` must point at the
exact same key-ring path *and* register the same application name (`"VeSessionManager"`, hardcoded
identically in both `Program.cs` files) — if these ever drift, one process's writes silently become
unreadable by the other. **This key ring needs the same backup discipline as the DB file** — if
it's ever lost while the DB survives, every encrypted credential becomes permanently unrecoverable
and every team has to re-enter Zoom/Square/SMTP/ExamTools credentials from scratch.

**But do not literally bundle the key ring into the same backup artifact as the DB.** The key file
itself is stored unencrypted on disk (Linux has no DPAPI-style at-rest protection for it, unlike
Windows), so the encryption only actually protects the credentials in a scenario where the key ring
and the DB end up in different hands. If both ever ship together in one archive and that archive
leaks, whoever has it can decrypt everything as trivially as if the columns were never encrypted —
back the key ring up somewhere separate from (or with tighter access than) wherever the DB backup
goes. See `docs/credential-encryption.md` for the full reasoning.

**Why `appsettings.Production.json` needs no manual server-side editing:** every real integration
credential (ExamTools/Zoom/Discord/Square/SMTP) lives per-`Team` in the database, hand-edited there
directly — never in appsettings (see `CLAUDE.md`'s "Optional-integration pattern" note). Both
`appsettings.Production.json` files already committed to this repo carry no secrets, so they're
synced automatically on every deploy like any other file — nothing to hand-maintain on the server,
unlike NcsScheduler where that file is deliberately excluded from sync because it *does* carry
secrets there.

---

## Build and Publish (manual, if ever needed outside CI)

```bash
dotnet publish src/VeSessionManager.Worker/VeSessionManager.Worker.csproj -c Release -o publish/worker
dotnet publish src/VeSessionManager.Web/VeSessionManager.Web.csproj -c Release -o publish/web
```

Then copy each folder to its own directory on the server (e.g. via `scp`/`rsync`).

---

## Automated Deploy (GitHub Actions)

`.github/workflows/deploy.yml` deploys automatically whenever a **version tag is pushed**
(`v*.*.*`) — never on every commit to `main` (that's `ci.yml`'s job: build + test only, on push/PR
against `main`). The runner joins the Tailscale network (the server has no public SSH access) as an
ephemeral node, then over SSH: backs up the SQLite DB, stops both services, `rsync`s each publish
output to its own subfolder (running remotely as root via `--rsync-path="sudo rsync"`, setting
final ownership inline with `--chown=vesessionmanager:vesessionmanager`), starts the **Worker
first** and confirms it's actually up before starting **Web** (both call
`dbContext.Database.Migrate()` at startup — starting them serially avoids a concurrent-migration
race against the shared SQLite file), then polls `http://localhost:5100/` for a response before
declaring success.

### One-time setup

**1. Tailscale OAuth client — nothing new to create**

This is the same Ubuntu box as `NcsScheduler`, so reuse its existing OAuth client (tagged
`tag:ci`, already permitted to reach the server in your tailnet ACLs). **GitHub Actions secrets are
per-repo, though** — even though the underlying Tailscale client is shared, you still need to add
`TS_OAUTH_CLIENT_ID`/`TS_OAUTH_SECRET` (the same values NcsScheduler's repo secrets already have) to
*this* repo's secrets too.

**2. Runtime service account**

A dedicated, unprivileged system account runs both services — not `www-data` (NcsScheduler's
account on the same box), so the two apps stay isolated from each other:

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin vesessionmanager
```

**3. Deploy user + SSH key — reuse `deploy`, add a new sudoers file**

Reuse the same `deploy` SSH account NcsScheduler's CI already logs in with (same keypair). It still
doesn't get filesystem access to `/opt/vesessionmanager` or `/var/lib/vesessionmanager` directly —
every operation that touches them (rsync, the DB backup, service start/stop, journalctl) runs as
root via a **new**, narrowly-scoped sudoers file specific to this app:

```bash
sudo tee /etc/sudoers.d/vesessionmanager-deploy > /dev/null <<'EOF'
Defaults:deploy !requiretty
deploy ALL=(root) NOPASSWD: /usr/bin/systemctl stop vesessionmanager-worker, /usr/bin/systemctl stop vesessionmanager-web, /usr/bin/systemctl start vesessionmanager-worker, /usr/bin/systemctl start vesessionmanager-web, /usr/bin/rsync *, /usr/bin/cp /var/lib/vesessionmanager/vesessionmanager.db *, /usr/bin/journalctl -u vesessionmanager-worker *, /usr/bin/journalctl -u vesessionmanager-web *
EOF
sudo chmod 0440 /etc/sudoers.d/vesessionmanager-deploy
sudo visudo -c
```

> `/etc/sudoers.d/` files **must** be mode `0440` — `tee` creates them with your default umask
> instead, so sudo silently ignores the file (every sudo call then falls back to demanding a
> password) until you `chmod` it. `visudo -c` will tell you if a file has the wrong permissions.
> `!requiretty` is defensive in case the server sets `Defaults requiretty` globally, which also
> breaks NOPASSWD sudo over a non-interactive SSH session. (Same gotcha NcsScheduler's own
> `docs/deployment.md` already documents — this file is a second, independent instance of it, not a
> replacement for NcsScheduler's own `ncsscheduler-deploy` file, which stays as-is.)

If `deploy`'s SSH key isn't already installed (it should be, from NcsScheduler's setup), install the
existing public key rather than generating a new keypair:

```bash
ssh-copy-id -i <existing deploy_key.pub> deploy@<server-tailscale-hostname>
```

**4. App/data directories**

```bash
sudo mkdir -p /opt/vesessionmanager/worker /opt/vesessionmanager/web /var/lib/vesessionmanager
sudo chown -R vesessionmanager:vesessionmanager /opt/vesessionmanager /var/lib/vesessionmanager
```

`dataprotection-keys/` doesn't need creating explicitly — the Data Protection API creates it itself
under `/var/lib/vesessionmanager/` (already owned by the service account above) the first time
either service starts.

**5. GitHub repo secrets** (Settings → Secrets and variables → Actions)

| Secret | Value |
|---|---|
| `TS_OAUTH_CLIENT_ID` | same value as NcsScheduler's repo secret |
| `TS_OAUTH_SECRET` | same value as NcsScheduler's repo secret |
| `SSH_PRIVATE_KEY` | contents of the shared `deploy` private key (same as NcsScheduler's repo secret) |
| `DEPLOY_HOST` | server's Tailscale hostname, e.g. `myserver.tailXXXX.ts.net` |
| `DEPLOY_USER` | `deploy` |

**Workflow constants** — non-sensitive, hardcoded in the `env:` block at the top of `deploy.yml`
rather than stored as secrets. Edit them there directly if your setup differs:

| Variable | Default | What it is |
|---|---|---|
| `DEPLOY_PATH` | `/opt/vesessionmanager` | Server directory each publish output is synced into (`worker/`/`web/` subfolders) |
| `DB_PATH` | `/var/lib/vesessionmanager/vesessionmanager.db` | Shared SQLite DB, backed up before every deploy |
| `WORKER_SERVICE` | `vesessionmanager-worker` | systemd service for the Worker |
| `WEB_SERVICE` | `vesessionmanager-web` | systemd service for the Web admin backend |
| `WEB_PORT` | `5100` | Local port the health check polls after restart — **placeholder**, change it (and the Web unit's `ASPNETCORE_URLS`) if it collides with anything else already running on the box (NcsScheduler already occupies 5000) |

### Triggering a deploy

Merge into `main` as usual (this only builds/tests via `ci.yml`), then:

```bash
git tag v0.1.0
git push --tags
```

That tag push is what triggers `deploy.yml` — never an ordinary commit to `main`.

---

## First sign-in on a fresh deployment (2026-08-01)

A Production database starts with **no account anyone can sign into**. `DevAuthSeeder` runs only in
Development, and every route that could create a user is itself `[Authorize]`d — so without one of
the two mechanisms below, nobody can sign in, and therefore nobody can create the account that would
let them sign in.

(Note the Worker's `DevDataSeeder` does create a `System` user with `Role = SystemAdmin`, but it has
no password and exists purely to own audit-trail foreign keys. It is not a way in.)

### Preferred: `--create-admin`

Password comes from the environment, so it never appears in shell history, in `ps` output, or in the
log:

```bash
VSM_ADMIN_PASSWORD='choose-something-long'   dotnet /opt/vesessionmanager/web/VeSessionManager.Web.dll --create-admin   --email you@example.org --name "Your Name" [--callsign WX0MIK]
```

Applies migrations first, so it works on a box where the services have never started. Exits without
starting the web host. Refuses if that email already exists.

### Fallback: the automatic bootstrap account

If the Web app starts and **no user has a password**, it creates a temporary SystemAdmin
`setup@vesessionmanager.local` with a **randomly generated password, printed once to the log at
`Warning`**:

```
[WRN] No account on this deployment could sign in, so a TEMPORARY SystemAdmin was created.
    Email:    setup@vesessionmanager.local
    Password: <generated>
```

Sign in, create your own account under Admin → Users, then **deactivate the bootstrap account**.
It keeps working — and that password stays valid — until you do.

Two deliberate choices here:

- **The password is generated per deployment, never a constant.** A fixed default would put identical
  known credentials on every deployment's internet-facing login page. `DevAuthSeeder.DevPassword` is
  fine because it is a throwaway dev fixture; this is not.
- **The guard is "can anyone sign in", not "does a user exist" or "is there a SystemAdmin".** The
  `System` audit user would satisfy both of the latter while leaving the deployment locked out —
  the same class of mistake as `DevAuthSeeder`'s original guard (CLAUDE.md, Known Constraints).

The trade-off: this writes a credential to the log, which nothing else in this codebase does. It is
accepted because a predictable password is worse and the account should live for minutes. Where you
have shell access anyway, prefer `--create-admin`, which never writes one.

## systemd Services

Example `/etc/systemd/system/vesessionmanager-worker.service`:

```ini
[Unit]
Description=VeSessionManager Worker
After=network.target

[Service]
WorkingDirectory=/opt/vesessionmanager/worker
ExecStart=/usr/bin/dotnet /opt/vesessionmanager/worker/VeSessionManager.Worker.dll
Restart=always
RestartSec=10
User=vesessionmanager
# The Worker is a plain generic Host, not ASP.NET Core -- it reads DOTNET_ENVIRONMENT, not
# ASPNETCORE_ENVIRONMENT (see CLAUDE.md's "Worker Service reads DOTNET_ENVIRONMENT" gotcha). Its
# own default when unset is Production anyway, but set it explicitly so it's never in question.
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

Example `/etc/systemd/system/vesessionmanager-web.service`:

```ini
[Unit]
Description=VeSessionManager Web
After=network.target

[Service]
WorkingDirectory=/opt/vesessionmanager/web
ExecStart=/usr/bin/dotnet /opt/vesessionmanager/web/VeSessionManager.Web.dll
Restart=always
RestartSec=10
User=vesessionmanager
Environment=ASPNETCORE_ENVIRONMENT=Production
# Without this, Kestrel falls back to its own default rather than the port Apache/the deploy
# health check expect -- set it explicitly so the real listening port is never in question. Must
# match WEB_PORT in deploy.yml.
Environment=ASPNETCORE_URLS=http://localhost:5100

[Install]
WantedBy=multi-user.target
```

Neither unit needs a `ConnectionStrings__DefaultConnection` override — both
`appsettings.Production.json` files already commit that value (`/var/lib/vesessionmanager/vesessionmanager.db`)
directly, since it carries no secret (unlike NcsScheduler, which sets its connection string via
systemd `Environment=` because its own deployment model treats it as more sensitive).

```bash
sudo systemctl daemon-reload
sudo systemctl enable vesessionmanager-worker vesessionmanager-web
sudo systemctl start vesessionmanager-worker
sudo systemctl start vesessionmanager-web
sudo systemctl status vesessionmanager-worker vesessionmanager-web
```

---

## Apache Virtual Host

The public domain for the Web admin backend is **`ve.wx0mik.radio`** (decided 2026-07-22). This is
independent of the deploy pipeline above, which only ever talks to `localhost:5100`.

```apache
<VirtualHost *:443>
    ServerName ve.wx0mik.radio

    ProxyPreserveHost On
    ProxyPass / http://localhost:5100/
    ProxyPassReverse / http://localhost:5100/

    SSLEngine on
    # ... Let's Encrypt cert paths
</VirtualHost>
```

**A second domain for a second team is still an open possibility, not yet needed.** This app is
multi-tenant behind one deployment — a second `Team` row (see `docs/multi-team.md`) is served by
this same Worker/Web pair, not a separate deploy — so a second domain here would be purely cosmetic
branding for that team's own users, not a functional requirement. If/when a second team wants their
own domain, add a second `<VirtualHost>` block with a different `ServerName`, pointing at the same
`localhost:5100` — no code or deploy change needed either way, and nothing here blocks on that
decision.

Enable required modules if not already active:

```bash
sudo a2enmod proxy proxy_http
sudo systemctl reload apache2
```
