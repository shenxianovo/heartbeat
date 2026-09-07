using Heartbeat.Collection.Hub.Collectors.Packages;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json.Serialization;

namespace Heartbeat.Collection.Headless;

public static class HeadlessManagementEndpoints
{
    public static IServiceCollection AddHeadlessManagement(this IServiceCollection services, HeadlessManagementOptions options)
    {
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(authentication =>
            {
                authentication.Authority = options.Authority;
                authentication.RequireHttpsMetadata = options.RequireHttpsMetadata;
                authentication.MapInboundClaims = false;
                authentication.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = !string.IsNullOrWhiteSpace(options.Audience),
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = options.Issuer,
                    ValidAudience = options.Audience,
                    ValidTypes = ["at+jwt"],
                    NameClaimType = "preferred_username"
                };
                authentication.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var clientId = context.Principal?.FindFirst("client_id")?.Value;
                        var subject = context.Principal?.FindFirst("sub")?.Value;
                        if (clientId != options.ClientId || subject != options.OwnerSubject)
                            context.Fail("Token does not belong to this Hub owner and client.");
                        return Task.CompletedTask;
                    }
                };
            });
        services.AddAuthorization();
        services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        return services;
    }

    public static void MapHeadlessManagement(this IEndpointRouteBuilder endpoints)
    {
        var management = endpoints.MapGroup("/hub/api/v1").RequireAuthorization();
        management.MapGet("/collectors", async (HeadlessCollectorReadModel fleet, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await fleet.BrowseAsync(cancellationToken)); }
            catch (HttpRequestException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        management.MapPost("/collectors/{packageId}/installation",
            (string packageId, ICollectorMarketplace marketplace) => Accept(() => marketplace.Install(packageId)));
        management.MapDelete("/collectors/{packageId}/installation",
            (string packageId, ICollectorMarketplace marketplace) => Accept(() => marketplace.Uninstall(packageId)));
        management.MapPost("/collectors/{packageId}/activation",
            (string packageId, ICollectorMarketplace marketplace) => Accept(() => marketplace.Retry(packageId)));
        management.MapPost("/collector-instances/{collectorInstanceId:guid}/authorization/{interactionId:guid}",
            (Guid collectorInstanceId, Guid interactionId, AuthorizationResponse request, ICollectorMarketplace marketplace) =>
                Accept(() => marketplace.SubmitAuthorization(collectorInstanceId, interactionId, request.Values)));
        management.MapGet("/operations", (ICollectorMarketplace marketplace) => Results.Ok(marketplace.OperationsSnapshot()));
        management.MapGet("/operations/{operationId:guid}", (Guid operationId, ICollectorMarketplace marketplace) =>
        {
            try { return Results.Ok(marketplace.GetOperation(operationId)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        management.MapPost("/operations/{operationId:guid}/cancellation", (Guid operationId, ICollectorMarketplace marketplace) =>
        {
            var result = marketplace.CancelOperation(operationId);
            return result switch
            {
                HostManagementOperationCancellation.NotFound => Results.NotFound(),
                HostManagementOperationCancellation.NotCancellable => Results.Conflict(result),
                _ => Results.Ok(result)
            };
        });
    }

    private static IResult Accept(Func<HostManagementOperation> submit)
    {
        try { return Accepted(submit()); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
    }

    private static IResult Accepted(HostManagementOperation operation) =>
        Results.Accepted($"/hub/api/v1/operations/{operation.OperationId:D}", operation);
}

public sealed record AuthorizationResponse(IReadOnlyDictionary<string, string> Values);
