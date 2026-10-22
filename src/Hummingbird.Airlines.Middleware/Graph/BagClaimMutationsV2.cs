using HotChocolate;
using HotChocolate.Types;
using Hummingbird.Airlines.Backend.Domain;
using Hummingbird.Airlines.Backend.Services;
using Hummingbird.Airlines.Middleware.Soap;
using Microsoft.Extensions.DependencyInjection;

namespace Hummingbird.Airlines.Middleware.Graph;

/// <summary>
/// Bag input for the v2 sibling schema (mapped at /graphql/v2): polymorphism via the oneOf
/// input-object pattern instead of the flat REST-shaped <see cref="BagInput"/> of v1. Exactly
/// one variant (checked or carryOn) must be supplied; HotChocolate enforces the oneOf contract
/// at coercion time, so invalid shapes are rejected as GraphQL errors instead of being silently
/// tolerated. Inner variant shapes stay field-for-field compatible with v1 (same optionality,
/// same domain mapping through BagMapper), so every verdict, code and warning is identical.
/// SDL for importers: GET /graphql/v2/schema.graphql (input BagInput @oneOf + variant inputs).
/// </summary>
[OneOf]
[GraphQLName("BagInput")]
public sealed class BagInputOneOf
{
    /// <summary>Checked (hold) luggage variant - exactly one variant per bag.</summary>
    public CheckedBagVariant? Checked { get; set; }

    /// <summary>Carry-on (cabin) luggage variant - exactly one variant per bag.</summary>
    public CarryOnBagVariant? CarryOn { get; set; }
}

/// <summary>Checked (hold) variant of the v2 bag input.</summary>
[GraphQLName("CheckedBagInput")]
public sealed class CheckedBagVariant
{
    /// <summary>Weight in kilograms; 0 &lt; kg &lt;= 100. In-allowance overweight keeps the bag and produces a warning instead of an error.</summary>
    public double WeightKg { get; set; }

    /// <summary>Free-text colour used to identify the bag at the belt (omit for empty).</summary>
    public string? Color { get; set; }

    /// <summary>Optional bag tag id; the system assigns HB-serial tags when omitted.</summary>
    public string? TagId { get; set; }

    /// <summary>Length in centimetres.</summary>
    public int? LengthCm { get; set; }

    /// <summary>Width in centimetres.</summary>
    public int? WidthCm { get; set; }

    /// <summary>Height in centimetres.</summary>
    public int? HeightCm { get; set; }

    /// <summary>Contains fragile items.</summary>
    public bool? Fragile { get; set; }
}

/// <summary>Carry-on (cabin) variant of the v2 bag input.</summary>
[GraphQLName("CarryOnBagInput")]
public sealed class CarryOnBagVariant
{
    /// <summary>Weight in kilograms; 0 &lt; kg &lt;= 100. In-allowance overweight keeps the bag and produces a warning instead of an error.</summary>
    public double WeightKg { get; set; }

    /// <summary>Free-text colour used to identify the bag at the belt (omit for empty).</summary>
    public string? Color { get; set; }

    /// <summary>Optional bag tag id; the system assigns HB-serial tags when omitted.</summary>
    public string? TagId { get; set; }

    /// <summary>Carries a laptop / large electronic device.</summary>
    public bool? HasLaptop { get; set; }

    /// <summary>Fits under the seat in front.</summary>
    public bool? FitsUnderSeat { get; set; }
}

/// <summary>Input of <c>checkIn</c> (v2 shape): flight registration with an optional bag list of
/// oneOf variants registered atomically with the boarding pass.</summary>
[GraphQLName("CheckInInput")]
public sealed class CheckInInputOneOf
{
    /// <summary>Six-character booking reference (case-insensitive), e.g. PRS205.</summary>
    public string BookingRef { get; set; } = string.Empty;

    /// <summary>Bags to register together with the check-in (optional).</summary>
    public List<BagInputOneOf>? Bags { get; set; }
}

/// <summary>Input of <c>dropBag</c> (v2 shape): one polymorphic bag at the bag drop.</summary>
[GraphQLName("DropBagInput")]
public sealed class DropBagInputOneOf
{
    /// <summary>Six-character booking reference (case-insensitive), e.g. PRS205.</summary>
    public string BookingRef { get; set; } = string.Empty;

    /// <summary>The bag to register (requires prior check-in).</summary>
    public BagInputOneOf Bag { get; set; } = new();
}

/// <summary>
/// Mutation root of the v2 sibling schema (SDL name <c>Mutation</c>): same four operations and
/// the same verdict/error-code vocabulary as v1, with checkIn/dropBag taking the oneOf bag
/// inputs. The resolvers flatten the variant into the v1 input and delegate to the v1 mutation
/// resolvers, so the legacy SOAP round-trips - and therefore every code, warning and tally -
/// come from exactly one implementation.
/// Spec importers find the root under SDL name <c>type Mutation</c> as in v1.
/// </summary>
[GraphQLName("Mutation")]
public sealed class MutationV2
{
    /// <summary>Check in a booking and optionally register variant bags atomically (boarding pass path).</summary>
    public CheckInPayload CheckIn(CheckInInputOneOf input, [Service] SoapGateway gateway) =>
        new Mutation().CheckIn(
            new CheckInInput
            {
                BookingRef = input.BookingRef,
                Bags = (input.Bags ?? []).Select(OneOfBagMapper.ToFlat).ToList(),
            },
            gateway);

    /// <summary>Register exactly one variant bag at the bag drop (requires prior check-in).</summary>
    public DropBagPayload DropBag(DropBagInputOneOf input, [Service] SoapGateway gateway) =>
        new Mutation().DropBag(
            new DropBagInput { BookingRef = input.BookingRef, Bag = OneOfBagMapper.ToFlat(input.Bag) },
            gateway);

    /// <summary>One make-up room scan; same flat input as v1 (scanner sessions are shape-neutral).</summary>
    public BagScanAck ScanBag(ScanBagInput input, [Service] BagScanSessionStore sessions) =>
        new Mutation().ScanBag(input, sessions);

    /// <summary>Discard a scanner session (idempotent; unknown ids answer removed=false).</summary>
    public ResetScanSessionPayload ResetScanSession(string sessionId, [Service] BagScanSessionStore sessions) =>
        new Mutation().ResetScanSession(sessionId, sessions);
}

/// <summary>Flattens a v2 oneOf bag into the v1 REST-shaped <see cref="BagInput"/> so both
/// schemas share one domain mapping path (BagMapper) and one SoapGateway route.</summary>
internal static class OneOfBagMapper
{
    public static BagInput ToFlat(BagInputOneOf input)
    {
        if (input.Checked is { } checkedBag)
        {
            return new BagInput
            {
                Kind = BagKind.Checked,
                WeightKg = checkedBag.WeightKg,
                Color = checkedBag.Color,
                TagId = checkedBag.TagId,
                LengthCm = checkedBag.LengthCm,
                WidthCm = checkedBag.WidthCm,
                HeightCm = checkedBag.HeightCm,
                Fragile = checkedBag.Fragile,
            };
        }

        if (input.CarryOn is { } carryOn)
        {
            return new BagInput
            {
                Kind = BagKind.CarryOn,
                WeightKg = carryOn.WeightKg,
                Color = carryOn.Color,
                TagId = carryOn.TagId,
                HasLaptop = carryOn.HasLaptop,
                FitsUnderSeat = carryOn.FitsUnderSeat,
            };
        }

        // Unreachable in practice: the oneOf coercion rule rejects zero-variant input before
        // the resolver runs. Kept defensive so the flat mapping never throws NullReference.
        throw new GraphQLException(
            ErrorBuilder.New()
                .SetMessage("Exactly one bag variant (checked or carryOn) is required.")
                .SetCode("INVALID_REQUEST")
                .Build());
    }
}

/// <summary>Registration of the oneOf sibling graph (query + mutation + subscription roots).
/// Shares the v1 BagScanSessionStore singleton; map with MapGraphQL("/graphql/v2", "OneOf").</summary>
public static class BagClaimGraphOneOfRegistration
{
    public static IServiceCollection AddBagClaimGraphOneOf(this IServiceCollection services) =>
        services
            .AddGraphQLServer("OneOf")
            .AddQueryType<Query>()
            .AddMutationType<MutationV2>()
            .AddSubscriptionType<Subscription>()
            .Services;
}
