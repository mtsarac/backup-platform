// Records host netlink events (timestamped) next to Chromium's NetLog, keeps both only when a run fails (#306).
import type { Reporter, FullResult } from '@playwright/test/reporter'
import { spawn, type ChildProcess } from 'node:child_process'
import { mkdirSync, rmSync, writeFileSync, createWriteStream } from 'node:fs'

const diagDir = process.env.E2E_DIAG_DIR ?? 'test-results/netdiag'

export default class DiagReporter implements Reporter {
  private mon?: ChildProcess

  onBegin() {
    rmSync(diagDir, { recursive: true, force: true })
    mkdirSync(diagDir, { recursive: true })
    // Wall and monotonic clock sampled together: NetLog ticks are monotonic, netlink lines are wall time.
    const mono = (Number(process.hrtime.bigint()) / 1e6).toFixed(0)
    writeFileSync(`${diagDir}/clock.txt`, `wall_ms=${Date.now()} monotonic_ms=${mono}\n`)
    this.mon = spawn('ip', ['-ts', 'monitor', 'link', 'address', 'route'], { stdio: ['ignore', 'pipe', 'ignore'] })
    this.mon.on('error', () => {}) // iproute2 missing: NetLog alone is still captured
    this.mon.stdout?.pipe(createWriteStream(`${diagDir}/netlink.log`))
  }

  onEnd(result: FullResult) {
    this.mon?.kill()
    if (result.status === 'passed') rmSync(diagDir, { recursive: true, force: true })
    else console.log(`network diagnostics kept in ${diagDir} (netlink.log, netlog.json.<pid> per browser launch, *.ns.log, clock.txt)`)
  }
}
