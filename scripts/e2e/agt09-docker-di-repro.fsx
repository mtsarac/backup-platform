#r "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin/Microsoft.Extensions.DependencyInjection.Abstractions.dll"
#r "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin/Microsoft.Extensions.DependencyInjection.dll"
#r "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin/Microsoft.Extensions.Http.dll"
#r "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin/Microsoft.Extensions.Logging.Abstractions.dll"
#r "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin/Microsoft.Extensions.Logging.dll"
#r "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin/Microsoft.Extensions.Options.dll"
#r "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin/Microsoft.Extensions.Diagnostics.Abstractions.dll"
#r "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin/Microsoft.Extensions.Diagnostics.dll"
#r "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin/BackupPlatform.Agent.Core.dll"
#r "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin/BackupPlatform.Agent.Transport.dll"
#r "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin/BackupPlatform.Contracts.dll"
open Microsoft.Extensions.DependencyInjection
let run (withHttp: bool) =
  let s = ServiceCollection()
  if withHttp then s.AddHttpClient() |> ignore
  s.AddSingleton<BackupPlatform.Agent.Core.Discovery.IDockerEngineTransport, BackupPlatform.Agent.Transport.DockerEngineHttpTransport>() |> ignore
  use p = s.BuildServiceProvider()
  let t = p.GetRequiredService<BackupPlatform.Agent.Core.Discovery.IDockerEngineTransport>()
  let r = t.QueryContainersAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult()
  printfn "withHttpClientFactory=%b -> %s" withHttp (if isNull r then "NULL" else string r.Length)
run false
run true
