# CS Server Watch

A Windows app for saving CS:GO and CS2 servers. Type an address. The app asks the server for its public Steam info before saving it. If it is not CS:GO or CS2, or if nothing answers, it is not saved.

## Start

Double-click `Start CS Server Watch.bat`, or run:

```text
publish\CsServerWatch.exe
```

Addresses can be `ip:port`, a bare host (port 27015), `connect host:port`, or a `steam://connect/...` link. A connect password in the link is ignored and not stored.

## What it shows

Saved servers keep their player count, map, ping, VAC, password flag, OS, server type, version, tags, and rules. Player names are requested too, but CS2 often hides them. Rules whose names contain password, rcon, token, secret, gslt, or steamaccount are shown as `(hidden)`.

Saved servers stay in the list when they do not answer. Press F5 or wait 15 seconds to refresh counts. Select a server to reload players and rules.

Servers are stored in `servers.json` next to this folder. The app uses only the public UDP server query. It does not use RCON and does not join the game.

## Release

Push a version tag to build the unpackaged app and attach `CsServerWatch-win-x64.zip` to a GitHub Release:

```text
git tag v1.0.0
git push origin v1.0.0
```

You can also run the **Build and release** workflow manually and enter a tag such as `v1.0.0`. The local `publish/` folder is gitignored. It is not committed, and the workflow does not upload that folder.
