# Privacy Policy — FileFerry

Effective: 2026-11-21 · app version 0.1.0

FileFerry is a folder-sync client for Android that keeps a folder on your device in step with a
folder on a Nextcloud server **that you own or are given access to**. This policy says, in plain
terms, what the app does with your data. The short version: the developer never sees it.

## 1. No data controller other than you

The developer of FileFerry does not collect, receive, store, or process any of your personal
data. There is no developer server, no developer account, no telemetry endpoint. The only two
systems that ever hold your files are **your device** and **your Nextcloud server**.

## 2. Nothing is collected

- no account with the developer, and no sign-in of any kind to a service of ours;
- no analytics, no ads, no tracking identifiers, no crash- or usage-reporting;
- no third-party SDKs that could communicate on their own — the app is built directly on the
  Android and .NET platforms;
- no e-mail address or name is ever requested.

## 3. What stays on your device

Everything the app needs to do its job is stored only on your device, in the app's private
storage:

| Data | Why it exists | Where |
|---|---|---|
| Your files and folders | the synced content itself | your device's file system |
| Sync baseline (last known state of both sides) | to compute what changed without re-reading everything | app's private storage |
| Sync log | so you can see what each run did, on-device only | app's private storage |
| Trash buffer | deleted files are copied here before any deletion is applied, so you can restore them | app's private storage |
| Server address and user name | so the app knows which server to talk to | app's private storage |
| Application token (an app password) | to authenticate to your server | the Android keystore-backed secure storage — never in logs, journals, settings files, or backups |

Uninstalling the app, or signing out inside it, deletes all of the above. Files on your device
and files on your server are never deleted by signing out or uninstalling.

## 4. The only network peer is your own server

The app opens network connections only to the Nextcloud server you add in the app. There is no
other destination, in foreground or in background.

- New folder pairs start out **LAN-only**: sync is attempted only when the server's host name is
  a local one. This is a default you can change per pair, and the effect stays the same: the
  conversation is still only with the server you named.
- Unencrypted HTTP is permitted only for local host names, for home servers that terminate TLS
  at a reverse proxy or run plain HTTP inside the home network. When you add a server over
  HTTPS, the connection is HTTPS.
- The sync runs in the background only for the folder pairs you configured, and only against
  that same server.

## 5. Permissions, and why

| Permission | Why it is requested |
|---|---|
| Internet | to reach the Nextcloud server you added |
| Ignore battery optimizations | optional, offered explicitly: without it the system may delay background sync runs |
| Foreground service (data sync) | to show a clear, ongoing notification while a transfer is active |
| Notifications | to report the status of long sync runs |

The app never asks for camera, microphone, location, contacts, or SMS.

## 6. Your controls

- **Sign out** removes the token, the folder-pair records, the baseline, and the logs from the
  device; your files stay on the server and on disk.
- **Delete the app** — nothing of yours remains on the device and nothing was ever held
  elsewhere.
- **Questions and reports** go to the public issue tracker linked from the app's About screen;
  please do not include server addresses or tokens in anything you post there.

## 7. This policy

Changes to the app's behavior toward your data will be reflected in this document, and the
effective date at the top will change with them. The in-app summary (About → Privacy) always
describes the same behavior in shorter words; this document is the full record.
