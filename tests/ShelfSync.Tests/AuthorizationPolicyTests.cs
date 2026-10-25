using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ShelfSync.Api.Security;

namespace ShelfSync.Tests;

public sealed class AuthorizationPolicyTests
{
    [Theory]
    [InlineData("Customer","ApplicationUser",true)]
    [InlineData("Customer","ReserveInventory",true)]
    [InlineData("Associate","ReserveInventory",false)]
    [InlineData("Manager","ReserveInventory",true)]
    [InlineData("Admin","ReserveInventory",true)]
    [InlineData("Customer","Staff",false)]
    [InlineData("Customer","Management",false)]
    [InlineData("Customer","Admin",false)]
    [InlineData("Associate","Staff",true)]
    [InlineData("Associate","Management",false)]
    [InlineData("Associate","Admin",false)]
    [InlineData("Manager","Staff",true)]
    [InlineData("Manager","Management",true)]
    [InlineData("Manager","Admin",false)]
    [InlineData("Admin","Staff",true)]
    [InlineData("Admin","Management",true)]
    [InlineData("Admin","Admin",true)]
    [InlineData("Unknown","ApplicationUser",false)]
    [InlineData("Anonymous","ApplicationUser",false)]
    public async Task Policies_EnforceThePermissionMatrix(string role,string policy,bool expected)
    {
        var builder=WebApplication.CreateBuilder();
        builder.Configuration["Operator:PasswordHash"]=new PasswordHasher<string>().HashPassword("operator","Test-only password");
        builder.Configuration["ConnectionStrings:ShelfSync"]="Host=localhost;Database=unused;Username=unused";
        builder.AddOperatorSecurity();
        await using var app=builder.Build();await using var scope=app.Services.CreateAsyncScope();
        var principal=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role,role)],role=="Anonymous"?null:"test"));
        var result=await scope.ServiceProvider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal,null,policy);
        Assert.Equal(expected,result.Succeeded);
    }
}
