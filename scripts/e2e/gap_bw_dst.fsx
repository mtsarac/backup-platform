#r "../../backup-backend/src/core/domain/bin/Release/net10.0/BackupPlatform.Domain.dll"
open System
open BackupPlatform.Domain.Backups
open BackupPlatform.Domain.Identity

let evaluate name zone day start finish instants =
    let id = Guid.NewGuid()
    let p = BandwidthProfile(id, TenantId.New(), name, zone, DateTimeOffset.UtcNow)
    p.SetWindows([ TransferScheduleWindow(Guid.NewGuid(), id, day, TimeOnly.Parse(start), TimeOnly.Parse(finish), 65536L) ])
    for instant in instants do
        let utc = DateTimeOffset.Parse(instant).ToUniversalTime()
        let struct (limit, next) = p.Evaluate(utc)
        printfn "%s | %s | %O | limit=%A | nextTransition=%A" name zone utc limit next
        if next.HasValue && next.Value <= utc then failwith "next transition must be strictly future"

evaluate "Berlin spring skipped 02:15-02:45" "Europe/Berlin" DayOfWeek.Sunday "02:15" "02:45" ["2027-03-28T00:59:59Z";"2027-03-28T01:00:00Z";"2027-03-28T01:30:00Z"]
evaluate "Berlin spring end skipped 01:30-02:30" "Europe/Berlin" DayOfWeek.Sunday "01:30" "02:30" ["2027-03-28T00:29:59Z";"2027-03-28T00:30:00Z";"2027-03-28T00:59:59Z";"2027-03-28T01:00:00Z"]
evaluate "Berlin fall repeated 02:15-02:45" "Europe/Berlin" DayOfWeek.Sunday "02:15" "02:45" ["2027-10-29T23:00:00Z";"2027-10-31T00:14:59Z";"2027-10-31T00:15:00Z";"2027-10-31T00:45:00Z";"2027-10-31T01:15:00Z";"2027-10-31T01:45:00Z"]
evaluate "Berlin fall midnight crossing" "Europe/Berlin" DayOfWeek.Saturday "23:00" "03:00" ["2027-10-30T20:59:59Z";"2027-10-30T21:00:00Z";"2027-10-30T22:00:00Z";"2027-10-31T00:59:59Z";"2027-10-31T01:00:00Z";"2027-10-31T02:00:00Z"]
evaluate "New York spring skipped" "America/New_York" DayOfWeek.Sunday "02:15" "02:45" ["2027-03-14T06:59:59Z";"2027-03-14T07:00:00Z"]
evaluate "New York fall repeated" "America/New_York" DayOfWeek.Sunday "01:15" "01:45" ["2027-11-07T05:14:59Z";"2027-11-07T05:15:00Z";"2027-11-07T05:45:00Z";"2027-11-07T06:15:00Z";"2027-11-07T06:45:00Z"]
evaluate "Lord Howe spring half-hour skipped" "Australia/Lord_Howe" DayOfWeek.Sunday "02:10" "02:20" ["2027-10-02T15:29:59Z";"2027-10-02T15:30:00Z"]
evaluate "Lord Howe fall half-hour repeated" "Australia/Lord_Howe" DayOfWeek.Sunday "01:40" "01:50" ["2027-04-03T14:39:59Z";"2027-04-03T14:40:00Z";"2027-04-03T14:50:00Z";"2027-04-03T15:10:00Z";"2027-04-03T15:20:00Z"]
