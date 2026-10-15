using Contracts.Harness;

namespace Contracts.Harness.Tests;

public class ContractAssertTests
{
    private sealed class Message
    {
        public long FiId { get; set; }
        public string? FiNa { get; set; }
        public int CuPe { get; set; }
    }

    [Fact]
    public void NoDefaults_AllFilled_Passes()
    {
        ContractAssert.NoDefaults(new Message { FiId = 1, FiNa = "x", CuPe = 2 });
    }

    [Fact]
    public void NoDefaults_DefaultMemberInAllowList_Passes()
    {
        ContractAssert.NoDefaults(new Message { FiId = 1, FiNa = "x" }, allowed: ["CuPe"]);
    }

    [Fact]
    public void NoDefaults_UnexpectedDefaults_ListsEveryOne()
    {
        // Act
        var error = Assert.Throws<ContractViolationException>(() => ContractAssert.NoDefaults(new Message()));

        // Assert
        Assert.Contains("3 consumer member(s)", error.Message);
        Assert.Contains("  CuPe", error.Message);
        Assert.Contains("  FiId", error.Message);
        Assert.Contains("  FiNa", error.Message);
    }

    [Fact]
    public void NoDefaults_AllowedMemberNowFilled_FailsAsStale()
    {
        // Act
        var error = Assert.Throws<ContractViolationException>(
            () => ContractAssert.NoDefaults(new Message { FiId = 1, FiNa = "x", CuPe = 2 }, allowed: ["CuPe"]));

        // Assert
        Assert.Contains("no longer default", error.Message);
        Assert.Contains("  CuPe", error.Message);
    }
}
