using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Contracts.Harness;

/// <summary>
/// A checker that fails today because of a filed defect, including one deliberately deferred
/// to a backlog ticket. It is skipped, with the ticket key as
/// the reason, unless <see cref="IncludeVariable"/> is <c>1</c>. <c>run.sh</c> always runs these
/// in a separate, non-gating pass and reports any that now pass, so a fix landing is noticed
/// without anyone remembering to look.
/// </summary>
/// <remarks>
/// Adds the traits <c>Category=KnownDefect</c> and <c>Defect=&lt;ticket&gt;</c>, so one ticket
/// runs with <c>CONTRACT_INCLUDE_KNOWN=1 dotnet test --filter "Defect=GM-902"</c>.
/// Like <see cref="GoldenFactAttribute"/>, it also skips when no goldens were emitted.
/// </remarks>
[TraitDiscoverer("Contracts.Harness.KnownDefectTraitDiscoverer", "Contracts.Harness")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class KnownDefectFactAttribute : FactAttribute, ITraitAttribute
{
    public const string IncludeVariable = "CONTRACT_INCLUDE_KNOWN";

    public KnownDefectFactAttribute(string ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            throw new ArgumentException("A known defect needs its ticket key.", nameof(ticket));

        Ticket = ticket;

        if (Environment.GetEnvironmentVariable(IncludeVariable) != "1")
            Skip = $"Known defect {ticket}. Set {IncludeVariable}=1 to run it.";
        else if (GoldenStore.FromEnvironment() is null)
            Skip = $"{GoldenStore.DirectoryVariable} is not set; run run.sh.";
    }

    public string Ticket { get; }
}

public sealed class KnownDefectTraitDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new("Category", "KnownDefect");
        yield return new("Defect", traitAttribute.GetConstructorArguments().Cast<string>().Single());
    }
}
