namespace Atelia.VersionStore.Smoke;

internal static class Program {
    private static int Main(string[] args) {
        try {
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
