# T1.0 direct-download feasibility experiment

This is a runnable transport experiment, **not a product feature or an approved
T1 transport**. It never loads the application configuration, databases, enrollment
identity, provider credentials, or secret files. There is no product authorization
in this laboratory; never expose its HTTP listener beyond loopback or use real
backup data here.

## Run

Prerequisites: Node.js >=18.20, npm, a Chromium browser with OPFS, and enough free
browser-profile disk space and origin quota for the selected size. The native
binding's platform package must support the machine. No global installation,
application deployment, or running Agent is needed.

```sh
npm ci --prefix scripts/experiments/t1-direct-download --ignore-scripts --no-audit --no-fund
node scripts/experiments/t1-direct-download/server.mjs 104857600
# Open http://127.0.0.1:18761 and click “Run disk experiment (OPFS)”.
```

One server invocation admits one offer; restart it for another experiment. Use
`T1_LAB_PORT` for independent runs. Stop with Ctrl-C; the server also stops its
owned native child. Example sizes/modes:

```sh
node scripts/experiments/t1-direct-download/server.mjs 4294983681
node scripts/experiments/t1-direct-download/server.mjs 10485761 --corrupt
```

The latter changes the last byte **after** the native SHA-256 update; the receiver
must fail with `ChecksumMismatch`, never `Verified`. Additional browser-console
entry points, on a fresh server/page:

```js
window.runT1({ blocked: true });                 // No ICE path; expect timeout, zero received bytes.
window.runT1({ cancelAt: 1048576, delayMs: 1 }); // Cancel under backpressure, not completion.
window.runT1({ delayMs: 2 });                   // Slow disk consumer.
```

`blocked` selects relay-only ICE **without any ICE servers** to deliberately
prevent connectivity. No TURN server is used. This is a negative transport check,
not a real symmetric-NAT or UDP-firewall experiment. The normal mode uses host
candidates only: no STUN, TURN, UPnP, static forwarding or public inbound HTTP port.
It does use ephemeral UDP sockets; it does not prove service firewall permissions.

## What is checked

- A separate native `libdatachannel` child generates synthetic 16 KiB chunks.
  The HTTP parent handles only static experiment assets, SDP/ICE, cancellation,
  and result metadata. There is no HTTP artifact-content route.
- Reliable ordered DataChannel, one MiB maximum application send/receive window;
  credits advance only after browser disk writes. Full-file `Blob`, `arrayBuffer`
  and all-file WebCrypto digest are not used.
- Native Node/OpenSSL SHA-256, browser WASM streaming SHA-256, then a second browser
  SHA-256 read of the actual closed disk file must agree with its exact length.
  A verified receiver receipt, not signaling or send completion, ends the sender.
- OPFS temporary files are removed after verification and on handled failure or
  cancellation. Failed cleanup is reported. Native inactivity is bounded to 120s;
  the browser transfer deadline is 15 minutes (10s in the no-path experiment).
- The page result (`window.t1Result`) contains outcome, hash, queue bound and ICE
  evidence; `GET /metrics` contains signaling JSON-body byte counts and native RSS,
  outstanding bytes and terminal state. Counters exclude HTTP headers, static
  assets and the metrics response itself. Additional polling changes the counters.
  Native wire counters/pair data may be cleared by the library on disconnect;
  they are diagnostic, not authoritative payload accounting.
- The server rejects a second offer, cross-origin/foreign-Host requests, offers
  above 32 KiB and unknown routes. These laboratory guards are **not** proof of
  tenant, Agent, lease, fingerprint/session binding or product replay protection.

The “user-selected file” button calls the real `showSaveFilePicker` from a user
gesture and uses its disk stream; it has no in-memory or HTTP fallback. Select a
**new disposable file**, never an existing file: this laboratory may replace the
selected file. A failed transfer is not exported by the OPFS mode; the selected-file
mode is only suitable for synthetic data and does not implement production atomic
publication. OPFS success alone is **not** a saved panel download. A hard browser
crash may leave OPFS data; profile/restart recovery is not implemented here.

## .NET Windows/Linux service experiment

`dotnet-sender/` uses the same bounded chunk/credit/completion protocol and browser
receiver, with `DataChannelDotnet` 1.3.1's native binding and .NET SHA-256. It is a
separate synthetic lab host, **not** the enrolled production Agent. The NuGet
package carries platform binaries; package publication does not prove suitability
or native-library provenance/security. Its pinned repository URL returned 404
during this investigation, so source/provenance review remains a selection gate.
The lab host disables default appsettings/user-secrets/environment configuration
loading; only its explicit command-line configuration is used. It never loads the
production Agent configuration. Native/platform libraries still use normal OS
runtime facilities; this is not an OS sandbox.


```sh
dotnet publish scripts/experiments/t1-direct-download/dotnet-sender/T1.DirectLab.csproj \
  -c Release -r linux-x64 --self-contained true -o /tmp/t1-lab-linux
dotnet publish scripts/experiments/t1-direct-download/dotnet-sender/T1.DirectLab.csproj \
  -c Release -r win-x64 --self-contained true -o /tmp/t1-lab-windows
T1_LAB_PORT=54368 node scripts/experiments/t1-direct-download/server.mjs 104857601 --remote
```

In remote mode the HTTP parent never spawns a Node payload sender. The .NET sender
polls `/agent-offer`, posts bounded SDP/ICE/metrics to `/agent-events` and observes
`/agent-state` cancellation. These routes are loopback-only laboratory control,
**not** a production credential/signaling API. Keep them on loopback, including
inside the VM. Use an authenticated SSH reverse tunnel for control only:

```sh
ssh -N -o ExitOnForwardFailure=yes \
  -R 127.0.0.1:54368:127.0.0.1:54368 <authorized-vm-alias>
```

Copy the self-contained publish directory into a newly created, unique,
owner-controlled VM test directory. Do not overwrite an existing directory or
Agent installation. On Linux, start it with a unique transient **user** service:

```sh
systemd-run --user --unit=t1-direct-lab-<unique-run> \
  --property=RuntimeMaxSec=1200 --property=UMask=0077 \
  /absolute/owned-test-directory/T1.DirectLab --control-url=http://127.0.0.1:54368
```

On Windows, register a unique manual service under `NT AUTHORITY\LocalService`,
using the absolute published `T1.DirectLab.exe` path, the loopback `--control-url`,
and `--service-name=<the-exact-new-service-name>`. Grant only read/execute access
to the new test binaries. Never reconfigure the existing Agent service. Check
the service account, PID/state and final control metadata, not just `sc start`.
The host stops after its single transfer; waiting/transfer lifetime is bounded.
When done, remove only this invocation's service/unit, tunnel and owned test
directory. Service registration/removal needs the user's VM test authorization.

After the service starts waiting, open the host's loopback page and run the disk
experiment. The payload must use a selected VM↔browser UDP candidate pair, **not**
the SSH tunnel. Keep hashes and bounded metrics; don't publish raw SDP/ICE secrets.

For a separate, explicit STUN-only run:

```sh
T1_LAB_PORT=54368 T1_LAB_STUN=stun:stun.l.google.com:19302 \
  node scripts/experiments/t1-direct-download/server.mjs 104857601 --remote
```

Only `stun:host:port` is accepted; TURN is rejected. The dependency learns endpoint
addresses, not file bytes. This is an operator-chosen experiment dependency, not
a settled production STUN service. The no-path mode deliberately ignores STUN.
Do not patch mDNS candidates or disable firewalls to report a successful ordinary
run. Explicit candidate substitution is a diagnostic only; a diagnostic success
does not close the default-browser/network acceptance gate.

## Gate boundary

The npm dependencies are pinned and confined to this directory. `node-datachannel`
(MPL-2.0) exposes native libdatachannel; `hash-wasm` (MIT) supplies incremental browser
SHA-256. The .NET experiment adds its own pinned NuGet dependencies, including
the project's existing Hosting/WindowsServices versions. None of these lab
dependencies are added to the production Agent or frontend. Neither transport,
native binding nor a Node runtime is selected for production by this experiment.

Results and remaining acceptance gates: `docs/plan/Proje_Durumu.md`, T1.0 entry.
Design and full lifecycle/cutover constraints:
`docs/plan/T1_0_Dogrudan_Transfer_ADR.md`. Both are intentionally local/ignored.
