using Amazon;

namespace Bearing.Data.DynamoDb;

/// <summary>An AWS region: the code a connection saves (<c>eu-west-1</c>) and the name shown beside it
/// (<c>Europe (Ireland)</c>).</summary>
public sealed record AwsRegion(string Code, string Name);

/// <summary>
/// The regions the AWS SDK knows by name, for the connection editor's Region suggestions — from the SDK's own
/// built-in table (<see cref="RegionEndpoint.EnumerableAllRegions"/>), so no file and no network.
/// <para>
/// The SDK adds to that table at run time: resolving a region it has no entry for (DynamoDB Local's dummy region,
/// a region newer than this SDK) registers it named "Unknown", and there is a private S3-only
/// <c>us-east-1-regional</c>. Those are left out, so the list is the SDK's named regions only. A region missing
/// from it is still typeable — the box is free text.
/// </para>
/// </summary>
public static class AwsRegions
{
    /// <summary>The named regions: the commercial partition first, then the others (China, GovCloud, …), each
    /// by code.</summary>
    public static IReadOnlyList<AwsRegion> All { get; } = Load();

    private static List<AwsRegion> Load()
        => RegionEndpoint.EnumerableAllRegions
            .Where(r => !string.IsNullOrWhiteSpace(r.SystemName) && !string.IsNullOrWhiteSpace(r.DisplayName))
            .Where(r => !r.SystemName.EndsWith("-regional", StringComparison.Ordinal))
            .Where(r => !r.DisplayName.Contains("Unknown", StringComparison.Ordinal) && r.DisplayName != "Global")
            .OrderBy(r => r.PartitionName == "aws" ? 0 : 1)
            .ThenBy(r => r.SystemName, StringComparer.Ordinal)
            .Select(r => new AwsRegion(r.SystemName, r.DisplayName))
            .ToList();
}
