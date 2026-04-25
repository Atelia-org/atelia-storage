namespace Atelia;

internal static class ResultContractErrors {
    internal static AteliaError UninitializedResult { get; } = new UninitializedResultError();

    internal static InvalidOperationException CreateUnwrapFailure(AteliaError error) {
        ArgumentNullException.ThrowIfNull(error);
        return new InvalidOperationException(
            $"Cannot get value from a failed result. Error: [{error.ErrorCode}] {error.Message}"
        );
    }

    private sealed record UninitializedResultError()
        : AteliaError(
            ErrorCode: "Primitives.ResultUninitialized",
            Message: "Result is in its default/uninitialized state. Create it via Success(...) or Failure(...).",
            RecoveryHint: "Avoid relying on default(Result<T>) and construct the result explicitly."
        );
}
