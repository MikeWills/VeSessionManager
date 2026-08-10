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
| Data Protection key ring | `/var/lib/vesessionmanager-keys/` — **a separate directory from the database, deliberately** (moved 2026-08-10, see below) |
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

### The key ring moved out of the database directory (2026-08-10)

It used to live at `/var/lib/vesessionmanager/dataprotection-keys/` — *inside* the same directory as
`vesessionmanager.db`. That satisfied "outside the app path, so `rsync --delete` can never touch it"
but not the point of the paragraph above: one `tar` of `/var/lib/vesessionmanager/`, one disk image,
one careless backup, and the ciphertext and the key that opens it travel together. Encrypting the
columns bought nothing in that scenario.

It is now **`/var/lib/vesessionmanager-keys/`**, a sibling directory with its own ownership and mode
`0700`.

#### Migrating an existing deployment — order matters

⚠️ **Copy the keys before deploying the config change.** If the new path is empty when a service
starts, Data Protection generates a *fresh* key, and `EncryptedStringConverter`'s legacy-plaintext
fallback means nothing throws — every credential just reads back as an opaque blob and every
integration fails for reasons that point anywhere but here.

`DataProtectionKeyRingGuard` now refuses to start the host in exactly that state, which converts a
silent, hard-to-diagnose outage into a startup crash naming the affected teams and columns. **Do not
treat that guard as permission to skip the copy** — it is the backstop, not the procedure.

```bash
# 1. On the server, BEFORE deploying the config change:
sudo mkdir -p /var/lib/vesessionmanager-keys
sudo cp /var/lib/vesessionmanager/dataprotection-keys/*.xml /var/lib/vesessionmanager-keys/
sudo chown -R vesessionmanager:vesessionmanager /var/lib/vesessionmanager-keys
sudo chmod 700 /var/lib/vesessionmanager-keys

# 2. Confirm the keys actually arrived — an empty directory is the failure mode:
sudo ls -l /var/lib/vesessionmanager-keys/

# 3. Only now deploy (tag a release). Both services pick up the new path together.

# 4. Verify: the startup log should read
#    "Data Protection key ring verified — N team(s), all stored credentials readable".
sudo journalctl -u vesessionmanager-worker -n 30 --no-pager | grep -i "key ring"

# 5. Confirm in the UI that a team's credentials still work (Team Settings shows them set, and the
#    next ingestion poll succeeds), then remove the old copy:
sudo rm -rf /var/lib/vesessionmanager/dataprotection-keys
```

**If step 4 shows the guard throwing instead:** stop, and put the original key ring back. Do **not**
"fix" it by re-entering credentials in Team Settings — that overwrites the originals with values
encrypted under the new key and makes the old ones unrecoverable for good.

#### Backups

Back up `/var/lib/vesessionmanager-keys/` **separately from, and not alongside**, the database
backup — that separation is now the whole point of the directory being separate. Losing it while the
database survives makes every stored credential permanently unrecoverable.

**Every deploy already takes a snapshot of both**, in `deploy.yml`, before it stops the services:

```bash
# Database — snapshot beside the .db file
sudo rsync --ignore-missing-args \
  /var/lib/vesessionmanager/vesessionmanager.db \
  /var/lib/vesessionmanager/vesessionmanager.db.bak-$(date +%Y%m%d%H%M%S)

# Key ring — snapshot INSIDE the key directory, never beside the database
sudo rsync -a --ignore-missing-args \
  /var/lib/vesessionmanager-keys/ \
  /var/lib/vesessionmanager-keys/.bak-$(date +%Y%m%d%H%M%S)/
```

Two things about those commands are deliberate and worth not "tidying up":

- **`rsync --ignore-missing-args`, not `cp`.** On a brand-new box neither path exists yet, and an
  unconditional `cp` made the very first deploy to any server fail before it had stopped anything
  (found live 2026-08-04). Guarding with `test -f` is not an option either: `sudo test` is not in the
  deploy user's sudoers allowlist, and an unprivileged `test -f` cannot read `/var/lib/vesessionmanager`
  at all — so it would report "missing" forever and silently skip every backup from then on, which is
  worse than the failure it replaces. `/usr/bin/rsync` *is* allowlisted, copies normally when the
  source is there, and no-ops cleanly when it is not.
- **The two snapshots go to different directories.** Putting the key ring's copy next to the database
  would rebuild the exact problem that separating them solved — one archive of one directory carrying
  both the ciphertext and the key.

⚠️ **These are rollback snapshots, not backups.** Both sit on the same disk as the thing they
protect, so they survive a bad deploy and nothing else. A real off-box backup of
`/var/lib/vesessionmanager-keys/` — to somewhere with tighter access than wherever the database
backup goes — is still a separate manual job, and is the one that matters if the box is lost.

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

Steps 2-4 below can be done by hand, or with a bootstrap script. **The example script at the end of
this section does all three**, is idempotent, and is the faster path on a fresh box. Read the
per-step notes first regardless — they explain *why* each piece is shaped the way it is, and several
of them record failures found on real deploys.

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

> **These rules are per-unit, and sudo matches the whole command line.** `systemctl stop
> vesessionmanager-web vesessionmanager-worker` matches *neither* single-unit rule and is rejected —
> confusingly, as `sudo: a password is required`, which reads like a broken SSH key rather than an
> allowlist miss. `deploy.yml` therefore issues one `systemctl` call per service; keep it that way
> rather than widening these rules, since their narrowness is what stops a compromised deploy key
> from touching anything else on the box.

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

The key ring directory is created explicitly and kept **out** of `/var/lib/vesessionmanager/`, so a
backup or disk image of the database directory cannot also carry the key that decrypts its contents:

```bash
sudo mkdir -p /var/lib/vesessionmanager-keys
sudo chown vesessionmanager:vesessionmanager /var/lib/vesessionmanager-keys
sudo chmod 700 /var/lib/vesessionmanager-keys
```

On a genuinely new deployment it can be left empty — Data Protection writes its first key there on
startup, and with no credentials stored yet there is nothing to fail to decrypt. **On an existing
deployment, see "The key ring moved out of the database directory" above: the keys must be copied
in before the services start against the new path.**

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
| `DB_PATH` | `/var/lib/vesessionmanager/vesessionmanager.db` | Shared SQLite DB, backed up before every deploy — via `rsync --ignore-missing-args`, so the first deploy to a new box (where the file doesn't exist yet) isn't a failure; see the step's own comment for why `test -f` can't be used here |
| `WORKER_SERVICE` | `vesessionmanager-worker` | systemd service for the Worker |
| `WEB_SERVICE` | `vesessionmanager-web` | systemd service for the Web admin backend |
| `WEB_PORT` | `5100` | Local port the health check polls after restart — **placeholder**, change it (and the Web unit's `ASPNETCORE_URLS`) if it collides with anything else already running on the box (NcsScheduler already occupies 5000) |

#### Example bootstrap script

Everything in steps 2-4 as one idempotent script. **Genericised on purpose** — set the variables at
the top and it works for any app on this pattern; the real one for this deployment lives outside the
repo (`ops/`, gitignored) so server-specific values are not published.

Run once, as root, before the first deploy: `sudo bash setup-server.sh`

```bash
#!/usr/bin/env bash
set -euo pipefail

# ---- Set these ----------------------------------------------------------------
APP_SLUG="myapp"                       # names the service account, units and paths
SERVICE_ACCOUNT="$APP_SLUG"            # runs the app; no login, no home directory
DEPLOY_USER="deploy"                   # the account CI logs in as
DEPLOY_PATH="/opt/${APP_SLUG}"         # published binaries; rsync --delete target
DATA_PATH="/var/lib/${APP_SLUG}"       # database — deliberately OUTSIDE DEPLOY_PATH
KEYRING_PATH="/var/lib/${APP_SLUG}-keys"  # Data Protection keys — a SIBLING of DATA_PATH, not a child
DB_FILE="${APP_SLUG}.db"
WORKER_SERVICE="${APP_SLUG}-worker"
WEB_SERVICE="${APP_SLUG}-web"
WEB_PORT="5100"                        # must match ASPNETCORE_URLS below and WEB_PORT in deploy.yml
# -------------------------------------------------------------------------------

[[ $EUID -eq 0 ]] || { echo "Run as root: sudo bash $0" >&2; exit 1; }

# 1. Service account — system account, no shell, no home. Nothing logs in as this.
id -u "$SERVICE_ACCOUNT" &>/dev/null \
  || useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_ACCOUNT"

# 2. Directories.
mkdir -p "${DEPLOY_PATH}/worker" "${DEPLOY_PATH}/web" "$DATA_PATH"
chown -R "${SERVICE_ACCOUNT}:${SERVICE_ACCOUNT}" "$DEPLOY_PATH" "$DATA_PATH"

# The key ring gets its own directory and 0700, not 0755. It decrypts the credential columns inside
# the database, so an archive of DATA_PATH must not be able to carry both halves.
mkdir -p "$KEYRING_PATH"
chown "${SERVICE_ACCOUNT}:${SERVICE_ACCOUNT}" "$KEYRING_PATH"
chmod 700 "$KEYRING_PATH"

# Upgrading a box that predates the split? Copy the keys BEFORE deploying the new config, or every
# credential silently reads back as ciphertext (the app treats undecryptable values as plaintext).
if [[ -d "${DATA_PATH}/dataprotection-keys" ]] && ! compgen -G "${KEYRING_PATH}/*.xml" > /dev/null; then
  echo "WARNING: keys still in ${DATA_PATH}/dataprotection-keys and ${KEYRING_PATH} is empty."
  echo "  sudo cp ${DATA_PATH}/dataprotection-keys/*.xml ${KEYRING_PATH}/"
  echo "  sudo chown ${SERVICE_ACCOUNT}:${SERVICE_ACCOUNT} ${KEYRING_PATH}/*.xml"
fi

# 3. Sudoers — one rule per unit, because sudo matches the WHOLE command line. A combined
#    "systemctl stop web worker" matches neither rule and is rejected as "a password is required",
#    which reads like a broken SSH key. Validate before installing: a malformed file in
#    /etc/sudoers.d can lock you out of sudo entirely.
SUDOERS_TMP="$(mktemp)"
cat > "$SUDOERS_TMP" <<EOF
Defaults:${DEPLOY_USER} !requiretty
${DEPLOY_USER} ALL=(root) NOPASSWD: /usr/bin/systemctl stop ${WORKER_SERVICE}, /usr/bin/systemctl stop ${WEB_SERVICE}, /usr/bin/systemctl start ${WORKER_SERVICE}, /usr/bin/systemctl start ${WEB_SERVICE}, /usr/bin/rsync *, /usr/bin/cp ${DATA_PATH}/${DB_FILE} *, /usr/bin/journalctl -u ${WORKER_SERVICE} *, /usr/bin/journalctl -u ${WEB_SERVICE} *
EOF
chmod 0440 "$SUDOERS_TMP"          # 0440 is mandatory — sudo silently ignores any other mode
visudo -c -f "$SUDOERS_TMP" >/dev/null \
  || { echo "sudoers validation failed; left at $SUDOERS_TMP, NOT installed" >&2; exit 1; }
mv "$SUDOERS_TMP" "/etc/sudoers.d/${APP_SLUG}-deploy"
chmod 0440 "/etc/sudoers.d/${APP_SLUG}-deploy"

# 4. systemd units.
cat > "/etc/systemd/system/${WORKER_SERVICE}.service" <<EOF
[Unit]
Description=${APP_SLUG} Worker
After=network.target

[Service]
WorkingDirectory=${DEPLOY_PATH}/worker
ExecStart=/usr/bin/dotnet ${DEPLOY_PATH}/worker/MyApp.Worker.dll
Restart=always
RestartSec=10
User=${SERVICE_ACCOUNT}
# A generic Host reads DOTNET_ENVIRONMENT, NOT ASPNETCORE_ENVIRONMENT. Set explicitly so it is
# never in question.
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
EOF

cat > "/etc/systemd/system/${WEB_SERVICE}.service" <<EOF
[Unit]
Description=${APP_SLUG} Web
After=network.target

[Service]
WorkingDirectory=${DEPLOY_PATH}/web
ExecStart=/usr/bin/dotnet ${DEPLOY_PATH}/web/MyApp.Web.dll
Restart=always
RestartSec=10
User=${SERVICE_ACCOUNT}
Environment=ASPNETCORE_ENVIRONMENT=Production
# Without this Kestrel picks its own port, not the one the proxy and health check expect.
Environment=ASPNETCORE_URLS=http://localhost:${WEB_PORT}

[Install]
WantedBy=multi-user.target
EOF

# Enabled, not started: nothing is published yet on a fresh box, so ExecStart would just fail. The
# first successful deploy starts them for real.
systemctl daemon-reload
systemctl enable "$WORKER_SERVICE" "$WEB_SERVICE"

cat <<SUMMARY
Bootstrap complete. Still manual:
  1. Install the CI deploy key into ${DEPLOY_USER}'s authorized_keys.
  2. Add the repo's Actions secrets (Tailscale client id/secret, SSH key, deploy host/user).
  3. Push a version tag to trigger the first deploy.
  4. Add the Apache vhost + TLS certificate once a domain exists.
  5. Back up ${KEYRING_PATH} OFF this box, separately from the database backup.
SUMMARY
```

Four things in there are load-bearing and should survive any tidying:

- **`chmod 0440` on the sudoers file, and `visudo -c` before installing it.** `tee`/`cat` create the
  file with your umask instead, and sudo *silently ignores* a file with any other mode — every sudo
  call then falls back to demanding a password. A malformed file can lock you out of sudo entirely,
  which is why it is validated in a temp location first.
- **One sudoers rule per systemd unit.** See the note above — the combined form matches nothing.
- **`KEYRING_PATH` as a sibling of `DATA_PATH`, at 0700.** A child directory puts the key inside any
  archive of the database directory, which is the whole thing the encryption is supposed to prevent.
- **Services enabled but not started.** There is no published app on a fresh box, so starting them
  here just produces a failed unit and a confusing first deploy.

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
Development, and every route that could create a user is itself `[Authorize]`d — so without the
command below, nobody can sign in, and therefore nobody can create the account that would let them.

(The Worker's `DevDataSeeder` does create a `System` user with `Role = SystemAdmin`, but it has no
password and exists purely to own audit-trail foreign keys. It is not a way in — which is also why
the "is anyone able to sign in?" check is written against `PasswordHash != null` rather than against
the role or a row count.)

```bash
dotnet /opt/vesessionmanager/web/VeSessionManager.Web.dll --create-admin --email you@example.org --name "Your Name" [--callsign WX0MIK]
```

Applies migrations first, so it works on a box where the services have never started. Prints a
generated password once to stdout, then exits without starting the web host. Refuses if that email
already exists.

To supply the password instead — scripted provisioning, or a password you have already chosen — set
`VSM_ADMIN_PASSWORD` in the environment. Never pass it as an argument: arguments are visible in shell
history and to anyone who can run `ps`.

**Nothing is seeded automatically.** An earlier design created a setup account with credentials
published in the README; that was reverted (2026-08-01) because it meant a documented username and
password worked on every deployment from first start until setup was finished.

**The Web app refuses to start until an administrator exists.** Rather than serving a login page
where every credential is rejected — which looks like a forgotten password or broken auth rather than
unfinished setup — it logs `Critical` and exits non-zero:

```
[CRT] Refusing to start: no account on this deployment can sign in. Create the first administrator with: ...
```

### Run `--create-admin` *before* starting the Web service on a new box

Because the unit is `Restart=always` / `RestartSec=10`, a Web service started before the administrator
exists will **restart-loop every ten seconds**, logging that line each time, until you create one. It
self-heals the moment you do — the next restart comes up normally — but on a fresh server the tidy
order is:

```bash
# after the files are in place, before starting vesessionmanager-web
dotnet /opt/vesessionmanager/web/VeSessionManager.Web.dll --create-admin --email you@example.org --name "Your Name"
sudo systemctl start vesessionmanager-web
```

`--create-admin` applies migrations itself, so it is safe to run before either service has ever
started. **The Worker is unaffected** and will happily poll ExamTools with no user accounts at all —
only the Web app has a login surface to protect.

Expect the first deploy to a brand-new server to report the Web unit as failed if you skip this: that
is the safeguard working, not a broken build.

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
