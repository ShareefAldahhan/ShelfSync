using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ShelfSync.Api.Accounts;

public static class AccountBootstrap
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration, CancellationToken ct=default)
    {
        await using var scope=services.CreateAsyncScope();
        var db=scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        var users=scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roles=scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(78314021)",ct);
        foreach(var role in AppRoles.All)
            if(!await roles.RoleExistsAsync(role)) Ensure(await roles.CreateAsync(new IdentityRole<Guid>(role){Id=Guid.NewGuid()}));
        var name=configuration["Operator:Username"]??"operator";
        var hash=configuration["Operator:PasswordHash"]??throw new InvalidOperationException("Configure bootstrap administrator credentials with Set-Operator.ps1.");
        var user=await users.FindByNameAsync(name);
        if(user is null)
        {
            user=new AppUser{Id=Guid.NewGuid(),UserName=name,PasswordHash=hash,SecurityStamp=Guid.NewGuid().ToString(),IsBootstrap=true};
            Ensure(await users.CreateAsync(user));Ensure(await users.AddToRoleAsync(user,AppRoles.Admin));
            db.AccessEvents.Add(new(){TargetUserId=user.Id,Action="BOOTSTRAP_ADMIN",Detail="Provisioned configured recovery administrator"});
        }
        else
        {
            if(!user.IsBootstrap) throw new InvalidOperationException("The bootstrap username conflicts with an existing non-bootstrap account.");
            if(user.PasswordHash!=hash)
            {
                user.PasswordHash=hash;user.LockoutEnd=null;user.AccessFailedCount=0;
                Ensure(await users.UpdateSecurityStampAsync(user));
                db.AccessEvents.Add(new(){TargetUserId=user.Id,Action="BOOTSTRAP_CREDENTIAL_ROTATION",Detail="Updated configured administrator credential"});
            }
        }
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
    public static void Ensure(IdentityResult result)
    {
        if(!result.Succeeded) throw new InvalidOperationException("Account operation failed: "+string.Join("; ",result.Errors.Select(e=>e.Description)));
    }
}
