#r "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin/BackupPlatform.Agent.Core.dll"
#r "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin/BackupPlatform.Agent.Transport.dll"
#r "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin/BackupPlatform.Contracts.dll"
open BackupPlatform.Agent.Core.Discovery
let t = new BackupPlatform.Agent.Transport.DockerEngineHttpTransport()
for inc in [true; false] do
  let r = DockerContainerDetector(t).DetectAsync(DiscoveryDetectionContext(inc), System.Threading.CancellationToken.None).GetAwaiter().GetResult()
  printfn "includeDocker=%b status=%s reason=%s count=%d" inc r.Status (string r.ReasonCode) r.Instances.Count
  for i in r.Instances |> Seq.sortBy (fun i -> i.ContainerName) do
    printfn "  %-18s %-9s port=%-6s reason=%s image=%s" i.ContainerName i.Engine (match i.Port with | p when p.HasValue -> string p.Value | _ -> "null") (string i.ReasonCode) i.ContainerImage
