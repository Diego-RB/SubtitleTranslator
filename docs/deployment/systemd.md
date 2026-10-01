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
# Optional overrides use ASP.NET Core's double-underscore configuration convention:
# Scanner__Directories__0=/media/streaming
# Scanner__Directories__1=/media/qbittorrent
```

Do not put secret values in the unit file, SQLite database, source control, or command-line arguments. Configure each provider's `CredentialId` to the corresponding environment-variable name. Rotate any credentials inherited from the legacy script.

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
```

`/health/live` confirms the web process is serving requests. `/health/ready` checks the SQLite-backed application readiness. `/health` remains the aggregate endpoint for existing probes.

Configure the reverse proxy to terminate TLS and forward to `127.0.0.1:8080`. Restrict dashboard access with the network controls or authentication provided by the deployment environment; the application does not yet implement user authentication.

## Updating

Publish to a new staging directory, stop the service, back up `/var/lib/subtitletranslator/subtitletranslator.db`, replace the application files, then restart:

```sh
sudo systemctl stop subtitletranslator
sudo cp -a /var/lib/subtitletranslator/subtitletranslator.db /var/lib/subtitletranslator/subtitletranslator.db.bak
sudo systemctl start subtitletranslator
```

The application applies EF Core migrations during startup. Keep a database backup before upgrades that change the schema.
