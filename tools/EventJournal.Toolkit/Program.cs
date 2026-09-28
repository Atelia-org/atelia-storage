using Atelia.EventJournal.Toolkit;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
return ToolkitCli.Run(args, Console.Out, cancellation.Token);
