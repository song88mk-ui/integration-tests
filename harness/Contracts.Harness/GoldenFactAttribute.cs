using Xunit;

namespace Contracts.Harness;

/// <summary>
/// A fact that is skipped unless <see cref="GoldenStore.DirectoryVariable"/> is set. Checkers
/// use it so a plain <c>dotnet test</c> without emitted goldens skips instead of failing;
/// <c>run.sh</c> always sets the variable, so there they run for real.
/// </summary>
public sealed class GoldenFactAttribute : FactAttribute
{
    public GoldenFactAttribute()
    {
        if (GoldenStore.FromEnvironment() is null)
            Skip = $"{GoldenStore.DirectoryVariable} is not set; run run.sh.";
    }
}
