namespace Contracts.Harness;

/// <summary>A consumer member still holds its default after reading a golden.</summary>
public sealed class ContractViolationException(string message) : Exception(message);

public static class ContractAssert
{
    /// <summary>
    /// Runs check against every golden and fails once with every failure, each labelled with
    /// its producer variant, so a broken variant cannot hide behind another.
    /// </summary>
    public static void ForEachGolden(IEnumerable<Golden> goldens, Action<Golden> check)
    {
        var failures = new List<string>();
        foreach (var golden in goldens)
        {
            try
            {
                check(golden);
            }
            catch (Exception ex)
            {
                var label = golden.Variant.Length == 0 ? golden.MessageType : $"{golden.MessageType}~{golden.Variant}";
                failures.Add($"[{golden.RoutingKey}/{label}] {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
            throw new ContractViolationException(string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Fails with every default-valued member listed, one per line. xUnit's collection asserts
    /// truncate after five items, which hides most of a real drift.
    /// </summary>
    /// <param name="allowed">
    /// Consumer members that legitimately stay default for this golden. Each needs a comment at
    /// the call site saying why. An allowed member that is no longer default fails too, so the
    /// list cannot go stale.
    /// </param>
    public static void NoDefaults(object? consumerMessage, IReadOnlyCollection<string>? allowed = null)
    {
        allowed ??= [];
        var defaults = DefaultValueScanner.Scan(consumerMessage);
        var unexpected = defaults.Except(allowed).ToList();
        var stale = allowed.Except(defaults).ToList();

        if (unexpected.Count == 0 && stale.Count == 0)
            return;

        var lines = new List<string>();
        if (unexpected.Count > 0)
        {
            lines.Add($"{unexpected.Count} consumer member(s) read nothing from the producer's payload:");
            lines.AddRange(unexpected.Select(path => "  " + path));
        }

        if (stale.Count > 0)
        {
            lines.Add($"{stale.Count} allowed member(s) are no longer default; remove them from the allow-list:");
            lines.AddRange(stale.Select(path => "  " + path));
        }

        throw new ContractViolationException(string.Join(Environment.NewLine, lines));
    }
}
