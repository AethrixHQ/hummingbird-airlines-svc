using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hummingbird.Airlines.Backend.Domain;
using Hummingbird.Airlines.Backend.Services;
using Hummingbird.Airlines.Backend.Storage;
using Hummingbird.Airlines.Middleware.Api;
using Hummingbird.Airlines.Middleware.Grpc;
using Hummingbird.Airlines.Middleware.Graph;
using Hummingbird.Airlines.Middleware.Soap;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SoapCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------------------
// Abuse protection: hard transport-level caps.
// -----------------------------------------------------------------------
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxConcurrentConnections = 400;
    options.Limits.MaxRequestBodySize =
        builder.Configuration.GetValue<long?>("Protection:MaxRequestBodySizeBytes") ?? 262_144;

    // Long-lived SSE connections sit in deliberate write silence between keepalives;
    // the default minimum response data rate (5 s grace) would abort them.
    options.Limits.MinResponseDataRate = null;

    // ---------------------------------------------------------------------
    // Port layout. Calling Kestrel's Listen() means app.Urls / ASPNETCORE_URLS
    // are fully ignored, so every port is resolved here in a single place -
    // which keeps the REST/SOAP ports and the gRPC HTTP/2 listener consistent:
    //   Linux App Service front end -> 8080 (set ASPNETCORE_HTTP_PORTS=8080),
    //   local development           -> launchSettings / fallback 5000.
    // ---------------------------------------------------------------------
    var publicPortRaw =
        Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")
        ?? Environment.GetEnvironmentVariable("PORT")
        ?? "5000";
    foreach (var portEntry in publicPortRaw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        options.ListenAnyIP(int.Parse(portEntry, CultureInfo.InvariantCulture),
            listen => listen.Protocols = HttpProtocols.Http1);
    }

    var internalUrl = builder.Configuration["InternalUrl"] ?? "http://127.0.0.1:5150";
    var internalUri = new Uri(internalUrl);
    var internalIp = IPAddress.TryParse(internalUri.Host, out var parsedIp)
        ? parsedIp
        : IPAddress.Loopback;
    options.Listen(internalIp, internalUri.Port, listen => listen.Protocols = HttpProtocols.Http1);

    // Dedicated cleartext HTTP/2 listener for gRPC: plain HTTP ports cannot negotiate
    // h2c, so gRPC gets its own port with HTTP/2 only (clients use prior knowledge).
    // Set Grpc:HttpPort to 0 to disable the gRPC transport entirely.
    var grpcPort = builder.Configuration.GetValue("Grpc:HttpPort", 5007);
    if (grpcPort is > 0)
    {
        options.ListenAnyIP(grpcPort, listen => listen.Protocols = HttpProtocols.Http2);
    }
});

// The service runs behind cloud proxies (Azure App Service front end) whose networks
// are unknown/unstable: trust any X-Forwarded-For chain. Per-IP quotas are keyed on
// the forwarded client address; spoofing it cannot exceed the server-wide ceiling.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = RateLimitResponses.WriteAsync;

    var protection = builder.Configuration.GetSection("Protection");
    var globalRpm = protection.GetValue("GlobalRequestsPerMinute", 3000);
    var apiRpm = protection.GetValue("ApiRequestsPerMinute", 120);
    var soapRpm = protection.GetValue("SoapRequestsPerMinute", 240);
    var otherRpm = protection.GetValue("OtherRequestsPerMinute", 120);
    var maxConcurrentPerIp = protection.GetValue("MaxConcurrentPerIp", 16);
    var internalToken = builder.Configuration["Internal:SharedSecret"] ?? string.Empty;

    bool IsInternalCall(HttpContext http) =>
        !string.IsNullOrEmpty(internalToken)
        && string.Equals(http.Request.Headers["X-HB-Internal-Token"], internalToken, StringComparison.Ordinal);

    var ceiling = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetFixedWindowLimiter("server-wide-ceiling",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = globalRpm, Window = TimeSpan.FromMinutes(1) }));

    var perCaller = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        if (IsInternalCall(http))
        {
            return RateLimitPartition.GetNoLimiter("internal");
        }

        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var (partition, permitPerMinute) = http.Request.Path.StartsWithSegments("/soap")
            ? ($"soap:{ip}", soapRpm)
            : http.Request.Path.StartsWithSegments("/api")
                ? ($"api:{ip}", apiRpm)
                : ($"other:{ip}", otherRpm);

        return RateLimitPartition.GetFixedWindowLimiter(partition,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitPerMinute, Window = TimeSpan.FromMinutes(1) });
    });

    var parallelCalls = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        if (IsInternalCall(http))
        {
            return RateLimitPartition.GetNoLimiter("internal-concurrency");
        }

        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetConcurrencyLimiter($"concurrency:{ip}",
            _ => new ConcurrencyLimiterOptions { PermitLimit = maxConcurrentPerIp, QueueLimit = 0 });
    });

    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(ceiling, perCaller, parallelCalls);
});


builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    });

builder.Services.AddHttpContextAccessor();

// gRPC server plumbing: the BaggageClaim service below plus server reflection so
// tooling (grpcurl, gRPC UI, ...) can enumerate it without a proto copy.
builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();
builder.Services.AddSingleton<BaggageClaimBoard>();

// GraphQL (HotChocolate): baggage claim queries + subscriptions over WebSocket at /graphql;
// the SDL is also served at /graphql/schema.graphql for spec importers.
builder.Services.AddBagClaimGraph();

// Sibling schema ("OneOf") at /graphql/v2: same roots/payloads, bag inputs as oneOf variants.
// Shares the v1 singletons (BagScanSessionStore), so scanner sessions are common to both.
builder.Services.AddBagClaimGraphOneOf();

// Gate turnaround feed (REST snapshot + interactive WebSocket for Terminal 2E departures).
builder.Services.AddSingleton<TurnaroundFeed>();

// Required by SoapCore >= 1.2 so FaultException<T> is serialized into a proper
// SOAP fault response instead of crashing while building the error message.
builder.Services.AddSingleton<SoapCore.Extensibility.IFaultExceptionTransformer>(
    new SoapCore.DefaultFaultExceptionTransformer<SoapCore.CustomMessage>());

builder.Services.AddSingleton<FlightScheduleStore>();
builder.Services.AddSingleton<BookingStore>();
builder.Services.AddSingleton<BookingSystemService>();
builder.Services.AddSingleton<FlightManagementService>();
builder.Services.AddSingleton<LuggageManagementService>();
builder.Services.AddSingleton<SoapGateway>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Hummingbird Airlines Gateway API",
        Version = "v1",
        Description = """
            REST gateway (EAI middleware) in front of three simulated SOAP 1.1 legacy systems:
              * BookingSystem   - reservations CRUD
              * FlightManagement - read-only flight control
              * LuggageManagement - check-in and baggage drop
            All business validation lives in the backends; the middleware only translates
            protocol, status codes and warnings.
            """,
    });

    // Baggage is polymorphic via a custom tolerant converter; Swashbuckle cannot see the
    // derived types without [JsonPolymorphic], so a schema filter builds the oneOf + discriminator.
    options.SchemaFilter<PolymorphicBaggageSchemaFilter>();

    // Feed <summary>/<example> XML comments into description/example metadata so
    // code generators (spec-driven object trees) can surface hints to the user.
    var xmlFiles = new[]
    {
        $"{typeof(Program).Assembly.GetName().Name}.xml",
        "Hummingbird.Airlines.Backend.xml",
    };
    foreach (var xmlFile in xmlFiles)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (File.Exists(path))
        {
            options.IncludeXmlComments(path, includeControllerXmlComments: true);
        }
    }
});

var app = builder.Build();

// Real client address (behind the Azure front end) before anything reads it.
app.UseForwardedHeaders();

// Reject abusive traffic before it reaches any endpoint, middleware or backend.
((IApplicationBuilder)app).UseRateLimiter();

app.UseMiddleware<LegacyExceptionMiddleware>();

// Default document + static assets for the service directory page. The content-type map
// is extended with YAML so the AsyncAPI spec under /specs/ is served (it is 404'd by the
// default provider because *.yaml is not in the built-in map).
app.UseDefaultFiles();
var staticContentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
staticContentTypes.Mappings[".yaml"] = "application/yaml";
staticContentTypes.Mappings[".yml"] = "application/yaml";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = staticContentTypes });

// Enables the WebSocket upgrade for every endpoint that opts in
// (the gate turnaround feed below; HotChocolate handles /graphql upgrades itself).
app.UseWebSockets();

// Interactive REST documentation (SOAP side publishes ?wsdl per endpoint).
app.UseSwagger();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Hummingbird Gateway v1"));

app.MapControllers();

// gRPC facade over the baggage claim board (unary + server streaming).
// Reachable only on the gRPC HTTP/2 listener configured above.
app.MapGrpcService<BaggageClaimGrpcService>();

// gRPC bidirectional bag scanner session (make-up room, client streams scans,
// server streams per-scan dispositions + a session summary on half-close).
app.MapGrpcService<BaggageScannerGrpcService>();

// gRPC server reflection (also HTTP/2 only) for discovery by external tools.
app.MapGrpcReflectionService();

// GraphQL waterhole: IDE/POST endpoint at /graphql (subscriptions over WebSocket there)
// plus a downloadable SDL for spec importers (spec-driven object model generation).
app.MapGraphQL("/graphql");
app.MapGraphQLSchema("/graphql/schema.graphql");

// GraphQL v2 sibling: same surface, bag inputs expressed as oneOf variants (checked/carryOn).
// The IDE rides the same GET behaviour; the v2 SDL is downloadable for spec importers.
app.MapGraphQL("/graphql/v2", "OneOf");
app.MapGraphQLSchema("/graphql/v2/schema.graphql", "OneOf");

// Gate turnaround feed: REST snapshot + interactive WebSocket.
app.MapGateFeed();

// Downloadable proto contracts for spec importers (most toolchains ship protoc and
// resolve google/protobuf/* well-known types themselves). The literal `.proto` suffix in the
// template matters: with the broader "/specs/{file}" pattern, endpoint routing would claim
// EVERYTHING under /specs/ (routing runs first and precedence-beats the static files
// middleware), and the AsyncAPI YAML next to the protos would 404.
app.MapGet("/specs/{protoName}.proto", (string protoName) =>
{
    var allowed = new[] { "baggageclaim", "baggagescanner" };
    if (!allowed.Contains(protoName, StringComparer.OrdinalIgnoreCase))
    {
        return Results.Problem(
            title: "Unknown spec file",
            detail: $"Serve one of: {string.Join(", ", allowed.Select(name => $"{name}.proto"))} (plus /specs/asyncapi.yaml and /graphql/schema.graphql).",
            statusCode: StatusCodes.Status404NotFound);
    }

    var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Protos", $"{protoName}.proto");
    return File.Exists(path)
        ? Results.File(path, "text/plain; charset=utf-8")
        : Results.Problem(
            title: "Spec file not deployed",
            detail: "The proto contracts are not deployed next to the binary (dev: dotnet run).",
            statusCode: StatusCodes.Status404NotFound);
});

// Current protection settings, rendered on the default page.
app.MapGet("/api/v1/_protection", (IConfiguration config) =>
{
    var p = config.GetSection("Protection");
    return Results.Json(new
    {
        globalRequestsPerMinute = p.GetValue("GlobalRequestsPerMinute", 3000),
        apiRequestsPerMinute = p.GetValue("ApiRequestsPerMinute", 120),
        soapRequestsPerMinute = p.GetValue("SoapRequestsPerMinute", 240),
        otherRequestsPerMinute = p.GetValue("OtherRequestsPerMinute", 120),
        maxConcurrentPerIp = p.GetValue("MaxConcurrentPerIp", 16),
        maxRequestBodySizeBytes = p.GetValue("MaxRequestBodySizeBytes", 262_144L),
    });
});

// The three simulated legacy endpoints (SOAP 1.1 over POST, WSDL on GET ?wsdl).
((IApplicationBuilder)app).UseSoapEndpoint<BookingSystemService>(options =>
{
    options.Path = "/soap/booking";
    options.SoapSerializer = SoapSerializer.DataContractSerializer;
    options.HttpGetEnabled = true;
    options.HttpsGetEnabled = true;
    options.IndentXml = true;
});
((IApplicationBuilder)app).UseSoapEndpoint<FlightManagementService>(options =>
{
    options.Path = "/soap/flights";
    options.SoapSerializer = SoapSerializer.DataContractSerializer;
    options.HttpGetEnabled = true;
    options.HttpsGetEnabled = true;
    options.IndentXml = true;
});
((IApplicationBuilder)app).UseSoapEndpoint<LuggageManagementService>(options =>
{
    options.Path = "/soap/luggage";
    options.SoapSerializer = SoapSerializer.DataContractSerializer;
    options.HttpGetEnabled = true;
    options.HttpsGetEnabled = true;
    options.IndentXml = true;
});

// Port layout note: all endpoints (public REST/SOAP ports, internal SOAP loopback and
// the gRPC HTTP/2 listener) are bound in ConfigureKestrel above. app.Urls is unused here
// on purpose - mixing both binding styles makes Kestrel drop the app.Urls set.

app.Run();


