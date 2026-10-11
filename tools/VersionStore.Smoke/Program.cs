namespace Atelia.VersionStore.Smoke;

internal static class Program {
    private static int Main(string[] args) {
        try {
            if (args.Length > 0 && args[0] is "dynamic-ref-worker" or "dynamic-ref-read") {
                return DynamicRefScenario.Run(args);
            }
            if (args.Length > 0 && args[0] is "tag-golden-write" or "tag-golden-read") {
                return TagGoldenScenario.Run(args);
            }
            if (args.Length > 0 && args[0] is "tool-worker" or "tool-recover" or "tool-read") {
                return ToolLoopScenario.Run(args);
            }
            if (args.Length > 0 && args[0] is "generation-write" or "generation-read") {
                return GenerationScenario.Run(args);
            }
            if (args.Length > 0 && args[0] is "resource-write" or "resource-read") {
                return ResourceScenario.Run(args);
            }
            if (args.Length > 0 && args[0] is "graph-write" or "graph-read") {
                if (args.Length != 2) {
                    throw new ArgumentException("Usage: graph-write <root> | graph-read <root>");
                }
                return GraphScenario.Run(args[0], args[1]);
            }
            return ProcessScenario.Run(args);
        }
        catch (Exception error) {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
