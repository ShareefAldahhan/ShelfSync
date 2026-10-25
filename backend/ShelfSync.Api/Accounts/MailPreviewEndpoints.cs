using Microsoft.AspNetCore.Identity;
using Npgsql;
using System.Text.Json;
namespace ShelfSync.Api.Accounts;
public sealed record ForgotPasswordRequest(string? Username);
public static class MailPreviewEndpoints
{
    public static void MapMailPreviewEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/forgot-password",async(ForgotPasswordRequest request,UserManager<AppUser> users,NpgsqlDataSource db,CancellationToken ct)=>
        {
            // Local mailbox is Admin-only. Never return a reset token to an anonymous caller.
            if(request.Username is {Length:>0 and <=64})
            {
                var user=await users.FindByNameAsync(request.Username.Trim());
                if(user is {Enabled:true,IsBootstrap:false})
                {
                    var code=await users.GeneratePasswordResetTokenAsync(user);
                    await using var cmd=db.CreateCommand("""INSERT INTO mail_preview(id,user_id,recipient,subject,body) VALUES($1,$2,$3,'Password recovery',$4)""");
                    cmd.Parameters.AddWithValue(Guid.NewGuid());cmd.Parameters.AddWithValue(user.Id);cmd.Parameters.AddWithValue(user.Email??user.UserName!);
                    cmd.Parameters.AddWithValue($"Local preview only; no email was sent. Verify the account holder before sharing. Username: {user.UserName}\nRecovery code (expires in 30 minutes):\n{code}\nUse Forgot password on the sign-in page. The operator account uses Set-Operator.ps1.");
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }
            return Results.Ok(new {message="If the account is eligible, a recovery message is available in the administrator's local email preview. No external email is sent. Contact your store administrator."});
        }).AllowAnonymous().RequireRateLimiting("login");
        app.MapGet("/api/admin/mail-preview",async(NpgsqlDataSource db,CancellationToken ct)=>
        {
            await using var cmd=db.CreateCommand("SELECT id,recipient,subject,body,created_at FROM mail_preview ORDER BY created_at DESC LIMIT 100");
            await using var r=await cmd.ExecuteReaderAsync(ct);var list=new List<object>();
            while(await r.ReadAsync(ct))list.Add(new{id=r.GetGuid(0),recipient=r.GetString(1),subject=r.GetString(2),body=r.GetString(3),createdAt=r.GetDateTime(4)});
            return Results.Ok(list);
        }).RequireAuthorization("Admin");
    }
}
