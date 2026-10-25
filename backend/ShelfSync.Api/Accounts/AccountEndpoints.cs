using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ShelfSync.Api.Accounts;

public sealed record NewAccountRequest(string Username,string Password,string Role);
public sealed record AccountChangeRequest(string Role,bool Enabled,string Reason);
public sealed record PasswordChangeRequest(string CurrentPassword,string NewPassword);
public sealed record PasswordResetRequest(string Password,string Reason);
public sealed record RecoveryCodeRequest(string Reason);
public sealed record RecoverPasswordRequest(string Username,string Code,string NewPassword);

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var admin=app.MapGroup("/api/admin").RequireAuthorization("Admin");
        admin.MapGet("/users",async(UserManager<AppUser> users,CancellationToken ct)=>
        {
            var list=await users.Users.OrderBy(u=>u.UserName).Take(100).ToListAsync(ct);
            var result=new List<object>();
            foreach(var u in list)result.Add(new {u.Id,username=u.UserName,u.Enabled,u.IsBootstrap,u.CreatedAt,roles=await users.GetRolesAsync(u)});
            return Results.Ok(result);
        });
        admin.MapPost("/users",async(NewAccountRequest request,HttpContext context,AccountsDbContext db,UserManager<AppUser> users,CancellationToken ct)=>
        {
            if(!ValidAccount(request))return Results.BadRequest(new {error="Choose a valid role, a username (3â€“64 characters), and a password (12â€“1024 characters)."});
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            var user=new AppUser{Id=Guid.NewGuid(),UserName=request.Username.Trim()};
            var created=await users.CreateAsync(user,request.Password);
            if(!created.Succeeded)return Failure(created);
            var assigned=await users.AddToRoleAsync(user,request.Role);if(!assigned.Succeeded)return Failure(assigned);
            db.AccessEvents.Add(new(){ActorUserId=UserId(context.User),TargetUserId=user.Id,Action="CREATE_ACCOUNT",Detail="Role: "+request.Role});
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Created($"/api/admin/users/{user.Id}",new {user.Id,username=user.UserName,role=request.Role});
        });
        admin.MapPost("/users/{id:guid}/access",async(Guid id,AccountChangeRequest request,HttpContext context,AccountsDbContext db,UserManager<AppUser> users,CancellationToken ct)=>
        {
            if(!AppRoles.All.Contains(request.Role)||string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Trim().Length is <3 or >500)return Results.BadRequest(new {error="A valid role and reason (3â€“500 characters) are required."});
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(78314021)",ct);
            var user=await users.FindByIdAsync(id.ToString());if(user is null)return Results.NotFound();
            if(user.IsBootstrap)return Results.Conflict(new {error="The recovery administrator is protected. Rotate its credentials using the local setup procedure."});
            if(id==UserId(context.User))return Results.Conflict(new {error="You cannot change your own account access from this screen."});
            var before=await users.GetRolesAsync(user);
            if(before.Contains(AppRoles.Admin)&&(!request.Enabled||request.Role!=AppRoles.Admin))
            {
                var admins=await users.GetUsersInRoleAsync(AppRoles.Admin);
                if(admins.Count(a=>a.Enabled)<=1)return Results.Conflict(new {error="At least one enabled administrator must remain."});
            }
            AccountBootstrap.Ensure(await users.RemoveFromRolesAsync(user,before));
            AccountBootstrap.Ensure(await users.AddToRoleAsync(user,request.Role));
            user.Enabled=request.Enabled;AccountBootstrap.Ensure(await users.UpdateSecurityStampAsync(user));
            db.AccessEvents.Add(new(){ActorUserId=UserId(context.User),TargetUserId=id,Action="CHANGE_ACCESS",Detail=$"{string.Join(',',before)} -> {request.Role}; enabled={request.Enabled}; {request.Reason.Trim()}"});
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Ok(new {updated=true});
        });
        admin.MapPost("/users/{id:guid}/password",async(Guid id,PasswordResetRequest request,HttpContext context,AccountsDbContext db,UserManager<AppUser> users,CancellationToken ct)=>
        {
            if(request.Password is null || request.Password.Length is <12 or >1024 || string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Length is <3 or >500)return Results.BadRequest(new {error="A new password (12â€“1024 characters) and reason (3â€“500 characters) are required."});
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            var user=await users.FindByIdAsync(id.ToString());if(user is null)return Results.NotFound();
            if(user.IsBootstrap)return Results.Conflict(new {error="Use Set-Operator.ps1 to rotate the recovery administrator password."});
            var token=await users.GeneratePasswordResetTokenAsync(user);var reset=await users.ResetPasswordAsync(user,token,request.Password);if(!reset.Succeeded)return Failure(reset);
            user.AccessFailedCount=0;user.LockoutEnd=null;AccountBootstrap.Ensure(await users.UpdateAsync(user));
            db.AccessEvents.Add(new(){ActorUserId=UserId(context.User),TargetUserId=id,Action="RESET_PASSWORD",Detail=request.Reason.Trim()});
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Ok(new {updated=true});
        });
        admin.MapPost("/users/{id:guid}/recovery-code",async(Guid id,RecoveryCodeRequest request,HttpContext context,AccountsDbContext db,UserManager<AppUser> users,CancellationToken ct)=>
        {
            if(string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Trim().Length is <3 or >500)return Results.BadRequest(new {error="Enter a verification reason (3–500 characters)."});
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            var user=await users.FindByIdAsync(id.ToString());
            if(user is null)return Results.NotFound();
            if(user.IsBootstrap||!user.Enabled)return Results.Conflict(new {error="Recovery codes cannot be issued for this account. Recovery administrators use the local setup script."});
            // Rotating the stamp invalidates earlier codes and sessions before issuing a replacement.
            AccountBootstrap.Ensure(await users.UpdateSecurityStampAsync(user));
            var code=await users.GeneratePasswordResetTokenAsync(user);
            db.AccessEvents.Add(new(){ActorUserId=UserId(context.User),TargetUserId=id,Action="ISSUE_RECOVERY_CODE",Detail=request.Reason.Trim()});
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
            return Results.Ok(new {code,username=user.UserName,expiresInMinutes=30});
        });
        app.MapPost("/api/auth/reset-password",async(RecoverPasswordRequest request,HttpContext context,AccountsDbContext db,UserManager<AppUser> users,CancellationToken ct)=>
        {
            const string failure="Unable to reset password. Check your username and recovery code, or ask an administrator for a new code.";
            if(string.IsNullOrWhiteSpace(request.Username)||request.Username.Length>64||string.IsNullOrWhiteSpace(request.Code)||request.Code.Length>4096||request.NewPassword is null||request.NewPassword.Length is <12 or >1024)
                return Results.BadRequest(new {error=failure});
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            var user=await users.FindByNameAsync(request.Username.Trim());
            if(user is null||!user.Enabled||user.IsBootstrap)return Results.BadRequest(new {error=failure});
            var result=await users.ResetPasswordAsync(user,request.Code.Trim(),request.NewPassword);
            if(!result.Succeeded)return Results.BadRequest(new {error=failure});
            user.AccessFailedCount=0;user.LockoutEnd=null;
            AccountBootstrap.Ensure(await users.UpdateAsync(user));
            db.AccessEvents.Add(new(){ActorUserId=user.Id,TargetUserId=user.Id,Action="RECOVER_PASSWORD",Detail="Password recovered using an administrator-issued code."});
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new {updated=true});
        }).AllowAnonymous().RequireRateLimiting("login");
        admin.MapGet("/audit",async(AccountsDbContext db,CancellationToken ct)=>Results.Ok(await db.AccessEvents.OrderByDescending(e=>e.CreatedAt).Take(100).ToListAsync(ct)));
        app.MapPost("/api/account/password",async(PasswordChangeRequest request,HttpContext context,AccountsDbContext db,UserManager<AppUser> users,CancellationToken ct)=>
        {
            if(request.NewPassword is null||request.NewPassword.Length is <12 or >1024||request.CurrentPassword is null||request.CurrentPassword.Length>1024)return Results.BadRequest(new {error="A current password and new password (12â€“1024 characters) are required."});
            var user=await users.GetUserAsync(context.User);if(user is null)return Results.Unauthorized();
            if(user.IsBootstrap)return Results.Conflict(new {error="Rotate this recovery account with Set-Operator.ps1."});
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            var changed=await users.ChangePasswordAsync(user,request.CurrentPassword,request.NewPassword);if(!changed.Succeeded)return Results.BadRequest(new {error="Unable to change password. Check the current password and new password requirements."});
            db.AccessEvents.Add(new(){ActorUserId=user.Id,TargetUserId=user.Id,Action="CHANGE_PASSWORD",Detail="User changed own password."});
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);return Results.Ok(new {updated=true});
        }).RequireAuthorization().RequireRateLimiting("login");
    }
    public static Guid UserId(ClaimsPrincipal user)=>Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static IResult Failure(IdentityResult result)=>Results.BadRequest(new {error=string.Join(" ",result.Errors.Select(e=>e.Description))});
    public static bool ValidAccount(NewAccountRequest request)=>!string.IsNullOrWhiteSpace(request.Username)&&request.Username.Trim().Length is >=3 and <=64&&request.Password is {Length:>=12 and <=1024}&&AppRoles.All.Contains(request.Role);
}
