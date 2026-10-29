using System.Security.Claims;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Lambda.AspNetCoreServer.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Restaurant.Api;

var builder = WebApplication.CreateBuilder(args);
// Uses API Gateway HTTP API payload v2 in Lambda; normal Kestrel behavior locally.
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
var authority = builder.Configuration["Authentication:Authority"];
var audience = builder.Configuration["Authentication:Audience"];
if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) || authorityUri.Scheme != Uri.UriSchemeHttps
    || string.IsNullOrWhiteSpace(audience))
    throw new InvalidOperationException("Set Authentication__Authority to your HTTPS OIDC issuer and Authentication__Audience to the API audience. No demo tokens are accepted.");

var isCognitoIssuer = authorityUri.Host.StartsWith("cognito-idp.", StringComparison.OrdinalIgnoreCase)
    && (authorityUri.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
        || authorityUri.Host.EndsWith(".amazonaws.com.cn", StringComparison.OrdinalIgnoreCase));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = authority;
    options.Audience = audience;
    options.RequireHttpsMetadata = true;
    options.MapInboundClaims = false;
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            // The provided Angular client sends its Cognito ID token, whose aud is the app client ID.
            // API Gateway audience validation alone does not distinguish Cognito token types.
            if (isCognitoIssuer && context.Principal?.FindFirst("token_use")?.Value != "id")
                context.Fail("A Cognito ID token is required.");
            return Task.CompletedTask;
        }
    };
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
        ValidateIssuerSigningKey = true, RequireSignedTokens = true,
        NameClaimType = "sub", ClockSkew = TimeSpan.FromSeconds(30)
    };
});
builder.Services.AddAuthorization();
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

if (builder.Configuration.GetValue<bool>("Storage:UseInMemory"))
{
    if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("In-memory storage is allowed only in Development.");
    var ownerSubject = builder.Configuration["Development:OwnerSubject"] ?? "workshop-owner-sub";
    builder.Services.AddSingleton<IGroupRepository>(new InMemoryGroupRepository([DevelopmentFixture.Create(ownerSubject)]));
}
else
{
    var tableName = builder.Configuration["DynamoDb:TableName"];
    if (string.IsNullOrWhiteSpace(tableName)) throw new InvalidOperationException("DynamoDb__TableName is required.");
    var region = builder.Configuration["AWS:Region"] ?? "eu-north-1";
    builder.Services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient(RegionEndpoint.GetBySystemName(region)));
    builder.Services.AddSingleton<IGroupRepository, DynamoGroupRepository>();
}
builder.Services.AddSingleton<GroupService>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (ApiException exception)
    {
        context.Response.StatusCode = exception.Status;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message }, context.RequestAborted);
    }
});
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
static string Subject(ClaimsPrincipal user)
    => user.FindFirstValue("sub") ?? throw new ApiException(401, "A subject claim is required.");

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/guest/groups/{groupReservationId}/sessions", async (string groupReservationId, ClaimsPrincipal user, GroupService service, CancellationToken cancellationToken)
    => Results.Ok(await service.GetSessionsAsync(groupReservationId, Subject(user), cancellationToken))).RequireAuthorization();
app.MapGet("/reservations/{reservationId}/guest-session-id", async (string reservationId, ClaimsPrincipal user, GroupService service, CancellationToken cancellationToken)
    => Results.Ok(await service.GetOwnerSessionAsync(reservationId, Subject(user), cancellationToken))).RequireAuthorization();
app.MapPost("/group-reservations/{groupReservationId}/submit", async (string groupReservationId, SubmitRequest request, ClaimsPrincipal user, GroupService service, CancellationToken cancellationToken)
    => Results.Ok(await service.SubmitAsync(groupReservationId, request.ReservationId, Subject(user), cancellationToken))).RequireAuthorization();
app.MapDelete("/guest/groups/{groupReservationId}/sessions/{guestSessionId}", async (string groupReservationId, string guestSessionId, ClaimsPrincipal user, GroupService service, CancellationToken cancellationToken)
    => Results.Ok(await service.RemoveAsync(groupReservationId, guestSessionId, Subject(user), cancellationToken))).RequireAuthorization();
app.MapGet("/guest/groups/{groupReservationId}/status", async (string groupReservationId, ClaimsPrincipal user, GroupService service, CancellationToken cancellationToken)
    => Results.Ok(await service.GetStatusAsync(groupReservationId, Subject(user), cancellationToken))).RequireAuthorization();
app.MapGet("/reservations/{reservationId}/invite-token", async (string reservationId, ClaimsPrincipal user, GroupService service, CancellationToken cancellationToken)
    => Results.Ok(new { token = await service.GetInviteTokenAsync(reservationId, Subject(user), cancellationToken) })).RequireAuthorization();
app.MapGet("/guest/invites/{token}", async (string token, GroupService service, CancellationToken cancellationToken)
    => Results.Ok(await service.GetInviteAsync(token, cancellationToken))).AllowAnonymous();
app.Run();

public partial class Program { }
