using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class HttpContractTests
{
    private sealed class ApiFactory(bool useCognitoIssuer = false, bool includeForeignGroup = false) : WebApplicationFactory<Program>
    {
        private readonly RSA rsa = RSA.Create(2048);
        private string Issuer => useCognitoIssuer
            ? "https://cognito-idp.eu-north-1.amazonaws.com/eu-north-1_Workshop"
            : "https://workshop-issuer.test";
        private const string Audience = "workshop-api";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development")
                .UseSetting("Authentication:Authority", Issuer)
                .UseSetting("Authentication:Audience", Audience)
                .UseSetting("Storage:UseInMemory", "true");
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    configuration.SigningKeys.Add(new RsaSecurityKey(rsa) { KeyId = "test-key" });
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
                if (includeForeignGroup)
                {
                    var fixture = DevelopmentFixture.Create();
                    var foreign = fixture with
                    {
                        Id = "foreign-group", ReservationId = "foreign-reservation",
                        OwnerSubject = "foreign-owner",
                        Participants = [fixture.Participants[1] with { Id = "foreign-session" }]
                    };
                    services.RemoveAll<IGroupRepository>();
                    services.AddSingleton<IGroupRepository>(new InMemoryGroupRepository([fixture, foreign]));
                }
            });
        }
        public string SignedToken(string subject, string? tokenUse = null)
        {
            var claims = new List<Claim> { new("sub", subject) };
            if (tokenUse is not null) claims.Add(new("token_use", tokenUse));
            var token = new JwtSecurityToken(Issuer, Audience, claims,
                notBefore: DateTime.UtcNow.AddMinutes(-1), expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) rsa.Dispose();
        }
    }

    [Fact]
    public async Task ProtectedEndpointRejectsAnonymousCaller()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/guest/groups/workshop-group/sessions")).StatusCode);
    }

    [Fact]
    public async Task UnsignedDemoTokenIsRejected()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var unsigned = new JwtSecurityToken("green-and-tasty-demo", "workshop-api", [new Claim("sub", "workshop-owner-sub")],
            expires: DateTime.UtcNow.AddMinutes(5));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(unsigned));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/guest/groups/workshop-group/sessions")).StatusCode);
    }

    [Fact]
    public async Task SignedForeignOwnerGets403()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("other-user"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/guest/groups/workshop-group/sessions")).StatusCode);
    }

    [Fact]
    public async Task SignedOwnerCanReadSubmitAndObserveUpdatedStatus()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("workshop-owner-sub"));
        var snapshot = await client.GetFromJsonAsync<GroupSnapshot>("/guest/groups/workshop-group/sessions");
        Assert.Equal(65m, snapshot!.Total);
        var response = await client.PostAsJsonAsync("/group-reservations/workshop-group/submit", new SubmitRequest("workshop-reservation"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<SubmittedDish[]>();
        Assert.Equal(3, items!.Single(item => item.DishId == "dish-salad").Quantity);
        Assert.Equal("Submitted", (await client.GetFromJsonAsync<GroupStatus>("/guest/groups/workshop-group/status"))!.Status);
        var invite = await client.GetAsync("/guest/invites/workshop-public-invite");
        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
        Assert.Contains("\"GroupReservationId\"", await invite.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CognitoIdTokenIsAccepted()
    {
        using var factory = new ApiFactory(useCognitoIssuer: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("workshop-owner-sub", "id"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/guest/groups/workshop-group/sessions")).StatusCode);
    }

    [Theory]
    [InlineData("access")]
    [InlineData("refresh")]
    [InlineData("")]
    [InlineData(null)]
    public async Task CognitoNonIdOrMissingTokenUseIsRejected(string? tokenUse)
    {
        using var factory = new ApiFactory(useCognitoIssuer: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("workshop-owner-sub", tokenUse));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/guest/groups/workshop-group/sessions")).StatusCode);
    }

    [Fact]
    public async Task DeleteRejectsAnonymousCaller()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/guest/groups/workshop-group/sessions/workshop-guest-session")).StatusCode);
    }

    [Fact]
    public async Task DeleteRejectsAuthenticatedNonOwnerWithoutChangingSnapshot()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("another-owner"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/guest/groups/workshop-group/sessions/workshop-guest-session")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("workshop-owner-sub"));
        Assert.Equal(3, (await client.GetFromJsonAsync<GroupSnapshot>("/guest/groups/workshop-group/sessions"))!.Participants.Length);
    }

    [Fact]
    public async Task DeleteMissingSessionReturns404AndReadableError()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("workshop-owner-sub"));
        var response = await client.DeleteAsync("/guest/groups/workshop-group/sessions/missing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Guest session not found.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeleteForeignGroupSessionReturns400WithoutDeletingEitherGroup()
    {
        using var factory = new ApiFactory(includeForeignGroup: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("workshop-owner-sub"));
        var response = await client.DeleteAsync("/guest/groups/workshop-group/sessions/foreign-session");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(3, (await client.GetFromJsonAsync<GroupSnapshot>("/guest/groups/workshop-group/sessions"))!.Participants.Length);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("foreign-owner"));
        Assert.Single((await client.GetFromJsonAsync<GroupSnapshot>("/guest/groups/foreign-group/sessions"))!.Participants);
    }

    [Fact]
    public async Task DeleteOwnGroupGuestReturnsUpdatedSnapshot()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.SignedToken("workshop-owner-sub"));
        var response = await client.DeleteAsync("/guest/groups/workshop-group/sessions/workshop-guest-session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await response.Content.ReadFromJsonAsync<GroupSnapshot>();
        Assert.Equal(2, snapshot!.Participants.Length);
        Assert.Equal(41m, snapshot.Total);
    }
}
