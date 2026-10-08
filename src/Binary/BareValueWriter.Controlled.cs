namespace Atelia.Binary;

public readonly partial struct BareValueWriter {
    /// <summary>Writes the exact frozen result of controlled value preparation without recompressing it.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The writer is uninitialized.</exception>
    /// <remarks>Output failures preserve the sink exception and do not roll back previously advanced bytes.</remarks>
    public void WritePreparedValue(ControlledValueEncodingPlan plan) {
        _ = GetDownstream();
        ArgumentNullException.ThrowIfNull(plan);
        plan.WriteTo(this);
    }
}
