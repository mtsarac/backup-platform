"""Second backend node on the same database for PERF-07: python3 api-replica.py up|down.
bp-api2 is a clone of bp-api (same image, environment, networks, volumes, so the same Data Protection keys) published on http://127.0.0.1:5254 and https://192.168.122.1:5255.
The frontend and the agents keep talking to bp-api; call bp-api2 directly to prove two nodes share scheduling, leases and idempotency."""
import json, subprocess, sys
NAME = "bp-api2"
act = sys.argv[1] if len(sys.argv) > 1 else sys.exit(__doc__)
subprocess.run(["docker", "rm", "-f", NAME], capture_output=True)
if act == "down": sys.exit()
src = json.loads(subprocess.check_output(["docker", "inspect", "bp-api"]))[0]
cmd = ["docker", "run", "-d", "--name", NAME, "--restart", "unless-stopped", "--network", next(iter(src["NetworkSettings"]["Networks"])),
       "-p", "127.0.0.1:5254:5000", "-p", "192.168.122.1:5255:5001"]
for e in src["Config"]["Env"]: cmd += ["-e", e]
for m in src["Mounts"]: cmd += ["-v", f"{m['Name'] if m['Type'] == 'volume' else m['Source']}:{m['Destination']}{':ro' if not m['RW'] else ''}"]
subprocess.run(cmd + [src["Config"]["Image"]], check=True)
