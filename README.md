# midea-dehumidifier

A small ASP.NET Core (.NET 10) backend that polls a Midea dehumidifier (MAD50PS1AWT-C) over your LAN, stores every
reading in SQLite, and serves a humidity-vs-time plot. It is modelled on the sibling `bryant-thermo` project.

## The plot

Open http://localhost:5090. The humidity line is coloured by how the reading compares to the **device's humidity setpoint**
and styled by whether the unit is running:

| Humidity vs. setpoint | Colour |
| --- | --- |
| more than 5 points below | red |
| within ±5 points (inclusive) | green |
| more than 5 points above | yellow |

| Unit state | Line |
| --- | --- |
| on (running flag set in the status frame) | solid |
| off | dashed |

The setpoint is drawn as a dotted line with a shaded ±5 band. A line segment takes the colour and dash of the reading it
ends at. Readings more than a few poll intervals apart are not joined, so outages show as gaps. Ranges: 6 h, 24 h, 7 d, 30 d.
Hover for details.

**Scrolling through time.** *◀ Back* / *Forward ▶* move the window by the step chosen in the dropdown (30 min, 1 h, 6 h, 12 h
or 24 h). *Now* returns to the live view, and *Forward* past the present does the same. While scrolled back the chart is
**paused** (the window is fixed, so it stops refreshing) and the toolbar shows the window's start and end; the
latest-reading summary at the top keeps updating. The window length and step are remembered in the browser.
The page needs no internet access (no CDN scripts).

"Motor on" is taken from the device's run/power flag. The raw status payload of every poll is stored (`raw_hex`), so if you
would rather key the dash on something else (e.g. fan speed or compressor state) it can be re-derived from history.

## How it gets the data

The dehumidifier speaks Midea's LAN protocol on TCP port 6444. This unit is a **V3** device: local access requires a
per-device *token* and *key*, which only Midea's cloud can issue. The backend:

1. Finds the appliance ID by sending a UDP discovery packet to the device (`Midea:Host`).
2. Gets the token/key **once** from the Midea cloud using your Midea app account, tries them against the device, and
   caches them in the SQLite `settings` table. After that the cloud is not contacted again unless the device rejects the cached key.
3. Every `PollIntervalSeconds` (default 60) opens a local connection, authenticates, asks for status, and appends a row to `readings`.

The protocol follows the open-source [msmart-ng](https://github.com/mill1000/midea-msmart) (LAN/cloud) and
[midea-beautiful-air](https://github.com/nbogojevic/midea-beautiful-air) (dehumidifier frames) projects. It is unofficial and
undocumented, so Midea can change it.

## Setup

1. Install the .NET 10 SDK.
2. Store your Midea app login as user secrets (kept out of the repo). `Midea:Cloud` is `SmartHome` for the Midea SmartHome /
   Midea Air apps, or `NetHomePlus` for NetHome Plus:

   ```powershell
   cd src\MideaDehumidifier.Api
   dotnet user-secrets set "Midea:Cloud" "SmartHome"
   dotnet user-secrets set "Midea:CloudAccount" "you@example.com"
   dotnet user-secrets set "Midea:CloudPassword" "your-password"
   ```

   Accounts created with Google/Apple sign-in have no password and cannot be used; create an email/password account instead
   (or set `Midea:Token` and `Midea:Key` directly if you already have them).
3. Run it (`Midea:Host` defaults to `192.168.68.114` in `appsettings.json`; give the device a DHCP reservation so it stays put):

   ```powershell
   dotnet run --project src\MideaDehumidifier.Api
   ```

   It listens on http://localhost:5090 (all interfaces). The database is `midea.db` in the project folder.
4. Run the tests: `dotnet test`

Once the first reading is stored you can remove the cloud account secrets; they are only used to fetch the token/key.
The token/key are stored unencrypted in the SQLite file, so protect it like a password.

### Run automatically on Windows

Open PowerShell as Administrator from the repository root:

```powershell
.\scripts\install-windows-service.ps1
```

It publishes the API to `Program Files\MideaDehumidifier`, keeps the database in `ProgramData\MideaDehumidifier\midea.db`,
and registers an automatically starting, auto-restarting service named `MideaDehumidifier`. If the token/key have not been
cached yet, first set machine-level environment variables `Midea__Cloud`, `Midea__CloudAccount` and `Midea__CloudPassword`
(they are copied into the service environment). Verify with `http://localhost:5090/api/health` or `Get-Service MideaDehumidifier`.
Remove it with `.\scripts\remove-windows-service.ps1` (the data is left in place).

## Configuration (`Midea` section)

| Key | Default | Meaning |
| --- | --- | --- |
| `Host` | `192.168.68.114` | Device IP / host name |
| `Port` | `6444` | LAN protocol port |
| `DeviceId` | `0` | Appliance ID; 0 = discover and cache |
| `Token`, `Key` | empty | Hex V3 credentials; empty = fetch from the cloud and cache |
| `Cloud` | `NetHomePlus` | `NetHomePlus` or `SmartHome` |
| `CloudRegion`, `CloudAccount`, `CloudPassword` | | Midea app login (only needed to fetch the token/key) |
| `PollIntervalSeconds` | `60` | Poll period (minimum 15) |
| `StaleAfterSeconds` | `300` | `/api/health` reports `degraded` when the last reading is older |
| `DatabasePath` | `midea.db` | SQLite file; relative paths resolve against the app folder |

## API

Interactive schema: http://localhost:5090/openapi/v1.json. All responses are JSON (camelCase).

| Method | Path | Returns |
| --- | --- | --- |
| GET | `/api/status` | Latest reading (503 until the first poll succeeds) |
| GET | `/api/readings?hours=24` | Stored readings, oldest first. Use `hours` (ending now) or `from` / `to` (ISO 8601). Long ranges are downsampled to about `maxPoints` (default 4000) while keeping every band / power change |
| GET | `/api/health` | `ok` / `degraded`, reading age, last error |

Each reading has `humidity`, `targetHumidity`, `band` (`low` / `normal` / `high`), `powerOn`, `temperatureC`, `mode`, `fanSpeed`,
`tankLevel`, `tankFull`, `defrosting` and `errorCode`.

## Troubleshooting

- `Midea cloud error 3101: Account or password incorrect`: the app account/password or `Midea:Cloud` is wrong
  (NetHome Plus and SmartHome accounts are separate). Failed token fetches are retried every 15 minutes to avoid locking the account.
- `device rejected every token/key`: `Midea:Cloud` does not match the app the device was set up in.
- No data but no error: confirm `Test-NetConnection 192.168.68.114 -Port 6444` succeeds.
