using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ShelfSync.Api.Accounts;

public sealed class AppUser : IdentityUser<Guid>
{
    public bool Enabled { get; set; } = true;
    public bool IsBootstrap { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public sealed class AccessAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ActorUserId { get; set; }
    public Guid TargetUserId { get; set; }
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : IdentityDbContext<AppUser,IdentityRole<Guid>,Guid>(options)
{
    public DbSet<AccessAudit> AccessEvents => Set<AccessAudit>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<AppUser>().ToTable("app_users");
        b.Entity<IdentityRole<Guid>>().ToTable("app_roles");
        b.Entity<IdentityUserRole<Guid>>().ToTable("app_user_roles");
        b.Entity<IdentityUserClaim<Guid>>().ToTable("app_user_claims");
        b.Entity<IdentityUserLogin<Guid>>().ToTable("app_user_logins");
        b.Entity<IdentityUserToken<Guid>>().ToTable("app_user_tokens");
        b.Entity<IdentityRoleClaim<Guid>>().ToTable("app_role_claims");
        b.Entity<AccessAudit>().ToTable("access_audit");
        b.Entity<AccessAudit>().Property(a=>a.Action).HasMaxLength(80);
        b.Entity<AccessAudit>().Property(a=>a.Detail).HasMaxLength(1000);
        b.Entity<AccessAudit>().HasIndex(a=>new {a.TargetUserId,a.CreatedAt});
    }
}
public static class AppRoles
{
    public const string Customer="Customer", Associate="Associate", Manager="Manager", Admin="Admin";
    public static readonly string[] All=[Customer,Associate,Manager,Admin];
    public static readonly string[] Staff=[Associate,Manager,Admin];
    public static readonly string[] Management=[Manager,Admin];
}
