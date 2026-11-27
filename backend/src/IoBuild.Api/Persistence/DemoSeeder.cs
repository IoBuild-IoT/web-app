using IoBuild.Api.CoreBusiness;
using IoBuild.Api.Devices.Application.Internal.CommandServices;
using IoBuild.Api.Publishing.Domain.Services;
using IoBuild.Api.Publishing.Domain.Services.Commands;
using IoBuild.Api.Workflows;
using Microsoft.EntityFrameworkCore;

namespace IoBuild.Api.Persistence;

/// <summary>
/// Progressive demo seed for the ESP32 prototype: builder and owner test accounts,
/// one unit and the three hardware devices (IDs fixed by the firmware's secrets.h).
/// Every step checks its own rule first and skips when it already holds, so it is safe
/// on every startup and on databases that already have part of the data.
/// Runs only with DemoSeed:Enabled=true and both passwords set (never stored in the repo).
/// </summary>
public static class DemoSeeder
{
    private sealed record HardwareDevice(int Id, string Type, string Label);

    public static async Task SeedAsync(IServiceProvider services, IConfiguration config, CancellationToken ct = default)
    {
        if (!config.GetValue<bool>("DemoSeed:Enabled")) return;

        var builderEmail = (config["DemoSeed:BuilderEmail"] ?? "builder.demo@iobuild.test").Trim().ToLowerInvariant();
        var ownerEmail = (config["DemoSeed:OwnerEmail"] ?? "owner.demo@iobuild.test").Trim().ToLowerInvariant();
        var builderPassword = config["DemoSeed:BuilderPassword"];
        var ownerPassword = config["DemoSeed:OwnerPassword"];
        var unitNumber = config["DemoSeed:UnitNumber"] ?? "101";
        var devices = new[]
        {
            new HardwareDevice(config.GetValue("DemoSeed:ClimateId", 16), "AirConditioner", "Air Conditioner"),
            new HardwareDevice(config.GetValue("DemoSeed:LightId", 17), "SmartLight", "Smart Light"),
            new HardwareDevice(config.GetValue("DemoSeed:NodeId", 18), "EdgeNode", "Edge Node (ESP32)"),
        };

        if (string.IsNullOrWhiteSpace(builderPassword) || string.IsNullOrWhiteSpace(ownerPassword))
        {
            Log("skipped: set DemoSeed__BuilderPassword and DemoSeed__OwnerPassword (min 8 chars).");
            return;
        }

        var db = services.GetRequiredService<IoBuildDbContext>();
        var register = services.GetRequiredService<IWorkflow<RegisterUser, int>>();
        var core = services.GetRequiredService<CoreBusinessService>();
        var units = services.GetRequiredService<IUnitCommandService>();
        var registry = services.GetRequiredService<DeviceRegistryService>();

        // 1. Builder account + profile + active subscription
        var builder = await db.IamUsers.SingleOrDefaultAsync(u => u.Email == builderEmail, ct);
        if (builder is null)
        {
            await register.ExecuteAsync(new RegisterUser(builderEmail, builderPassword, "Builder"), ct);
            builder = await db.IamUsers.SingleAsync(u => u.Email == builderEmail, ct);
            Log($"builder {builderEmail} created (id {builder.Id}).");
        }
        if (!await db.Profiles.AnyAsync(p => p.UserId == builder.Id, ct))
        {
            await core.CreateProfileAsync(builder.Id, "IoBuild Demo Builder", "builder.demo", cancellationToken: ct, yearsInBusiness: 5);
            Log("builder profile created.");
        }
        if (!await db.Subscriptions.AnyAsync(s => s.BuilderId == builder.Id && s.Status == "active", ct))
        {
            var plan = await db.Plans.OrderByDescending(p => p.Price).FirstAsync(ct);
            db.Subscriptions.Add(new Subscription { BuilderId = builder.Id, PlanId = plan.Id, Status = "active" });
            await db.SaveChangesAsync(ct);
            Log($"builder subscription created (plan {plan.Name}).");
        }

        // 2. Unit for the hardware: reuse the unit any hardware device already lives in,
        //    otherwise the builder's demo project + unit.
        var ids = devices.Select(d => d.Id).ToArray();
        var unitId = await db.Devices.Where(d => ids.Contains(d.Id) && d.UnitId != null).Select(d => d.UnitId).FirstOrDefaultAsync(ct);
        var unit = unitId is null ? null : await db.Units.FindAsync([unitId.Value], ct);
        if (unit is null)
        {
            var project = await db.Projects.FirstOrDefaultAsync(p => p.BuilderId == builder.Id, ct)
                ?? await core.CreateProjectAsync("IoBuild Demo", "Prototype project wired to the ESP32 edge node", "Lima", 1, builder.Id, null, ct);
            unit = await db.Units.FirstOrDefaultAsync(u => u.ProjectId == project.Id && u.UnitNumber == unitNumber, ct);
            if (unit is null)
            {
                var newId = await units.Handle(new CreateUnitCommand(project.Id, unitNumber, null, 1, "01"), ct);
                unit = await db.Units.SingleAsync(u => u.Id == newId, ct);
                Log($"unit {unitNumber} created (id {unit.Id}) in project {project.Name}.");
            }
        }

        // 3. Owner email on the unit (only when the unit has nobody yet)
        if (string.IsNullOrWhiteSpace(unit.OwnerEmail) && unit.OwnerId is null)
        {
            await units.Handle(new AssignUnitOwnerEmailCommand(unit.Id, ownerEmail), ct);
            Log($"unit {unit.UnitNumber} assigned to {ownerEmail}.");
        }
        await db.Entry(unit).ReloadAsync(ct);

        // 4. Hardware devices with the exact IDs the firmware publishes
        foreach (var hw in devices)
        {
            var existing = await db.Devices.FindAsync([hw.Id], ct);
            if (existing is not null)
            {
                if (existing.Type != hw.Type || existing.UnitId != unit.Id)
                    Log($"WARNING device {hw.Id} exists as {existing.Type} in unit {existing.UnitId}; expected {hw.Type} in unit {unit.Id}. Left untouched.");
                continue;
            }
            var sameType = await db.Devices.FirstOrDefaultAsync(d => d.UnitId == unit.Id && d.Type == hw.Type, ct);
            if (sameType is not null)
            {
                Log($"WARNING unit {unit.Id} already has a {hw.Type} (id {sameType.Id}); firmware expects id {hw.Id}. Left untouched.");
                continue;
            }

            var ownerId = unit.OwnerId ?? builder.Id;
            var device = new Device { Id = hw.Id, Name = $"{hw.Label} - Unit {unit.UnitNumber}", Type = hw.Type, Location = $"Unit {unit.UnitNumber}", ProjectId = unit.ProjectId, UnitId = unit.Id, Status = "online", OwnerId = ownerId };
            db.Devices.Add(device);
            db.DeviceProjections.Add(new DeviceProjection { DeviceId = hw.Id, ProjectId = unit.ProjectId, UnitId = unit.Id, DeviceName = device.Name, DeviceType = hw.Type, Status = device.Status, OwnerUserId = ownerId, LastEventAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            await registry.AnnounceAsync(device, ct);
            Log($"device {hw.Id} {hw.Type} created in unit {unit.UnitNumber}.");
        }

        // 5. Owner account (registration links unit, projections and devices to it)
        var unitOwnerEmail = unit.OwnerEmail?.Trim().ToLowerInvariant();
        if (unitOwnerEmail != ownerEmail)
        {
            Log($"owner skipped: unit {unit.UnitNumber} belongs to {unitOwnerEmail ?? $"user {unit.OwnerId}"}.");
        }
        else
        {
            var owner = await db.IamUsers.SingleOrDefaultAsync(u => u.Email == ownerEmail, ct);
            if (owner is null)
            {
                await register.ExecuteAsync(new RegisterUser(ownerEmail, ownerPassword, "Owner"), ct);
                owner = await db.IamUsers.SingleAsync(u => u.Email == ownerEmail, ct);
                Log($"owner {ownerEmail} created (id {owner.Id}).");
            }
            if (!await db.Profiles.AnyAsync(p => p.UserId == owner.Id, ct))
            {
                await core.CreateProfileAsync(owner.Id, "IoBuild Demo Owner", "owner.demo", cancellationToken: ct);
                Log("owner profile created.");
            }
        }

        Log("done.");
    }

    private static void Log(string message) => Console.WriteLine($"[DemoSeed] {message}");
}
