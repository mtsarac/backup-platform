using System;
using BackupPlatform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20261009140000_BackendRelayedRestoreSources")]
partial class BackendRelayedRestoreSources
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        // Target model configuration matches PlatformDbContextModelSnapshot.
#pragma warning restore 612, 618
    }
}
