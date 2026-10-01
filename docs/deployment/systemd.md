# Linux systemd deployment

This guide publishes the ASP.NET Core application as a single Linux service. The dashboard listens on loopback port `8080`; expose it through a TLS-terminating reverse proxy rather than directly to the public network.

## 1. Publish the application

Build on a machine with the .NET 10 SDK, or publish for the target Linux runtime:

```sh
dotnet publish src/SubtitleTranslator/SubtitleTranslator.csproj -c Release -o ./publish
```

Copy the published files to `/opt/subtitletranslator` on the server. Install the matching .NET 10 ASP.NET Core runtime on the server unless publishing self-contained.

## 2. Create the service account and directories

```sh
sudo useradd --system --home-dir /var/lib/subtitletranslator --shell /usr/sbin/nologin subtitletranslator
sudo install -d -o root -g root -m 0755 /opt/subtitletranslator
sudo install -d -o root -g root -m 0755 /etc/subtitletranslator
```

The unit's `StateDirectory` creates `/var/lib/subtitletranslator` with the service account as owner. SQLite data is stored there and survives app upgrades.

## 3. Configure environment

Create `/etc/subtitletranslator/subtitletranslator.env` with owner `root:root` and mode `0600`. Example (replace paths and secret references for the deployment):

```ini
ConnectionStrings__Default=Data Source=/var/lib/subtitletranslator/subtitletranslator.db
AllowedHosts=translator.example.com;localhost
OPENROUTER_API_KEY=replace-with-a-rotated-secret
Dashboard__AccessToken=replace-with-a-unique-random-token-of-at-least-32-characters
Metrics__AccessToken=replace-with-a-different-unique-random-token-of-at-least-32-characters
# Optional overrides use ASP.NET Core's double-underscore configuration convention:
# Scanner__Directories__0=/media/streaming
# Scanner__Directories__1=/media/qbittorrent
```

Do not put secret values in the unit file, SQLite database, source control, or command-line arguments. Configure each provider's `CredentialId` to the corresponding environment-variable name. Rotate any credentials inherited from the legacy script.

Generate the two access tokens independently (for example, `openssl rand -hex 32`) and replace both placeholders before starting the service. Production startup fails if either token is missing or shorter than 32 characters. The dashboard token is entered on `/Login` and creates an eight-hour HttpOnly/Secure cookie; the metrics token is sent as an HTTP Bearer credential. This is a single shared operator token, not per-user identity or audit logging.

Login POSTs are limited to five attempts per minute per client IP; logout is not counted. Forwarded client IPs are trusted only from loopback and explicitly configured proxy IPs. If a reverse proxy runs elsewhere (including a separate container), add its IP using `ReverseProxy__KnownProxies__0`; never trust arbitrary forwarded headers from untrusted clients.

The service account needs read access to the configured subtitle directories and write access to the output locations. With the supplied hardening, application state writes are restricted to `/var/lib/subtitletranslator`. If media directories are private to another group, grant the service account read/traverse permissions using a dedicated group or ACL; do not make media writable by the service unless output generation requires it.

## 4. Install and start the unit

Copy `deploy/systemd/subtitletranslator.service` to `/etc/systemd/system/subtitletranslator.service`, then run:

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now subtitletranslator
sudo systemctl status subtitletranslator
```

The unit restarts the process after failures and waits 30 seconds before a restart. Application logs go to journald:

```sh
sudo journalctl -u subtitletranslator -f
```

## 5. Verify health

From the server, check:

```sh
curl --fail http://127.0.0.1:8080/health/live
curl --fail http://127.0.0.1:8080/health/ready
curl --fail http://127.0.0.1:8080/health
read -rsp "Metrics token: " METRICS_TOKEN; echo
curl --fail -H "Authorization: Bearer ${METRICS_TOKEN}" http://127.0.0.1:8080/metrics
unset METRICS_TOKEN
```

`/health/live` confirms the web process is serving requests. `/health/ready` checks the SQLite-backed application readiness. `/health` remains the aggregate endpoint for existing probes.

The background provider monitor runs every 60 seconds with a 5-second per-probe timeout by default. Override these values with `ProviderHealth__Enabled`, `ProviderHealth__IntervalSeconds`, and `ProviderHealth__TimeoutSeconds` in the environment file. Results are held in memory and shown on the dashboard; after a restart they remain `UNKNOWN` until the first probe completes. Azure checks endpoint/authentication reachability, not an individual deployment's inference availability.

`/metrics` returns Prometheus text-format aggregates for job/chunk states, translation outcomes, retries, token usage, latency, estimated costs, and current provider health. It requires the `Metrics__AccessToken` bearer token. `/health`, `/health/live`, and `/health/ready` remain unauthenticated for infrastructure probes.

Prometheus alert rules are provided in `deploy/monitoring/subtitletranslator-alerts.yml`. Add that file to the Prometheus `rule_files` configuration and scrape `/metrics`. A same-host Prometheus can use `127.0.0.1:8080` with `authorization.credentials_file` pointing to a file containing only the metrics token, owned by the Prometheus service account and mode `0600`. For remote scraping, use a private network or authenticated proxy; do not expose `/metrics` publicly. The rules cover scrape failure, stalled jobs/batches, recent failed attempts, and offline/degraded provider probes. Route notifications through your own Alertmanager configuration.

Configure the reverse proxy to terminate TLS and forward to `127.0.0.1:8080`; operators sign in at `/Login` using the dashboard token. The app's dashboard cookie is Secure in non-development environments. Keep the public health routes minimal and do not expose `/metrics` through a public proxy route.

## Updating

Publish to a new staging directory, stop the service, back up `/var/lib/subtitletranslator/subtitletranslator.db`, replace the application files, then restart:

```sh
sudo systemctl stop subtitletranslator
sudo cp -a /var/lib/subtitletranslator/subtitletranslator.db /var/lib/subtitletranslator/subtitletranslator.db.bak
sudo systemctl start subtitletranslator
```

The application applies EF Core migrations during startup. Keep a database backup before upgrades that change the schema.
