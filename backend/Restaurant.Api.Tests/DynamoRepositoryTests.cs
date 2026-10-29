using Amazon;
using Amazon.Runtime;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Configuration;
using Restaurant.Api;
using Xunit;

namespace Restaurant.Api.Tests;

public sealed class DynamoRepositoryTests
{
    private sealed class RecordingClient : AmazonDynamoDBClient
    {
        public RecordingClient() : base(new AnonymousAWSCredentials(), RegionEndpoint.EUNorth1) { }
        public TransactWriteItemsRequest? LastWrite { get; private set; }
        public bool FailCondition { get; set; }
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
        {
            LastWrite = request;
            if (FailCondition) throw new TransactionCanceledException("conflict")
            {
                CancellationReasons = [new CancellationReason { Code = "ConditionalCheckFailed" }]
            };
            return Task.FromResult(new TransactWriteItemsResponse());
        }
    }

    private static DynamoGroupRepository Create(RecordingClient client)
        => new(client, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DynamoDb:TableName"] = "test-table" }).Build());

    [Fact]
    public async Task GroupAndReservationConditionAreInOneTransaction()
    {
        using var client = new RecordingClient();
        var fixture = DevelopmentFixture.Create() with { Version = 1, Status = "Submitted", OrderStatus = "SubmittedByCustomer" };
        Assert.True(await Create(client).SaveAsync(fixture, 0, null, default));
        Assert.Equal(2, client.LastWrite!.TransactItems.Count);
        Assert.Equal("#version = :expected", client.LastWrite.TransactItems[0].Put.ConditionExpression);
        Assert.Equal("0", client.LastWrite.TransactItems[0].Put.ExpressionAttributeValues[":expected"].N);
        Assert.Contains("SubmittedByCustomer", client.LastWrite.TransactItems[0].Put.Item["data"].S);
        Assert.Equal("RESERVATION#workshop-reservation", client.LastWrite.TransactItems[1].ConditionCheck.Key["PK"].S);
    }

    [Fact]
    public async Task DeleteSessionLookupSharesTheAtomicTransaction()
    {
        using var client = new RecordingClient();
        Assert.True(await Create(client).SaveAsync(DevelopmentFixture.Create() with { Version = 1 }, 0, "session-to-delete", default));
        Assert.Equal(3, client.LastWrite!.TransactItems.Count);
        Assert.Equal("SESSION#session-to-delete", client.LastWrite.TransactItems[2].Delete.Key["PK"].S);
        Assert.Equal("groupId = :group", client.LastWrite.TransactItems[2].Delete.ConditionExpression);
    }

    [Fact]
    public async Task ConditionalFailureIsReportedAsConflict()
    {
        using var client = new RecordingClient { FailCondition = true };
        Assert.False(await Create(client).SaveAsync(DevelopmentFixture.Create() with { Version = 1 }, 0, null, default));
    }
}
