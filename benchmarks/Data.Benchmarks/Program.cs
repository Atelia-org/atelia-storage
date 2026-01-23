using BenchmarkDotNet.Running;

var summary = BenchmarkRunner.Run<RollingCrc32CBenchmarks>();
